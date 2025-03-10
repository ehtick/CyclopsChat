using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Cyclops.Core;
using Cyclops.Core.Helpers;
using Cyclops.Xmpp.Client;
using Cyclops.Xmpp.Data;
using Cyclops.Xmpp.Protocol;
using Cyclops.Xmpp.SharpXmpp.Data;
using Cyclops.Xmpp.SharpXmpp.Errors;
using Cyclops.Xmpp.SharpXmpp.Protocol;
using SharpXMPP;
using SharpXMPP.XMPP;
using SharpXMPP.XMPP.Client.Elements;
using Namespaces = Cyclops.Xmpp.Protocol.Namespaces;

namespace Cyclops.Xmpp.SharpXmpp.Client;

public class SharpXmppClient : IXmppClient
{
    private readonly ILogger logger;
    private readonly SharpXmppIqQueryManager iqQueryManager;
    private readonly SharpXmppBookmarkManager bookmarkManager;

    private XmppClient? currentClient;

    public SharpXmppClient(ILogger logger)
    {
        this.logger = logger;
        iqQueryManager = new SharpXmppIqQueryManager();
        bookmarkManager = new SharpXmppBookmarkManager(logger);
        ConferenceManager = new SharpXmppConferenceManager(logger, this);
    }

    public void Dispose()
    {
        currentClient?.Dispose();
    }

    public IIqQueryManager IqQueryManager => iqQueryManager;
    public IBookmarkManager BookmarkManager => bookmarkManager;
    public IConferenceManager ConferenceManager { get; }

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? ReadRawMessage;
    public event EventHandler<string>? WriteRawMessage;
    public event EventHandler<Exception>? Error;
    public event EventHandler? StreamError;
    public event EventHandler? Authenticated;
    public event EventHandler? AuthenticationError;
    public event EventHandler<IPresence>? Presence;
    public event EventHandler? RoomMessage;
    public event EventHandler<IMessage>? Message;

    private volatile bool isAuthenticated;
    public bool IsAuthenticated
    {
        get => isAuthenticated;
        private set => isAuthenticated = value;
    }

    public void Connect(string server, string host, string user, string password, int port, string resource)
    {
        IsAuthenticated = false;
        if (currentClient != null)
        {
            UnsubscribeFromEvents(currentClient);
            currentClient?.Dispose();
        }

        currentClient = new XmppClient(new JID($"{user}@{server}"), password, autoPresence: false);
        SubscribeToEvents(currentClient);
        iqQueryManager.IqManager = currentClient.IqManager;
        bookmarkManager.Connection = currentClient;

        DoConnect().NoAwait(logger);
        async Task DoConnect()
        {
            try
            {
                await currentClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                AuthenticationError?.Invoke(this, EventArgs.Empty);
                Error?.Invoke(this, ex);
                throw;
            }
        }
    }

    private void SubscribeToEvents(XmppConnection connection)
    {
        connection.StreamStart += OnStreamStart;
        connection.Element += OnElement;
        connection.SignedIn += OnSignedIn;
        connection.Presence += OnPresence;
        connection.Message += OnMessage;
        connection.ConnectionFailed += OnConnectionFailed;
    }

    private void UnsubscribeFromEvents(XmppConnection connection)
    {
        connection.StreamStart -= OnStreamStart;
        connection.Element -= OnElement;
        connection.SignedIn -= OnSignedIn;
        connection.Presence -= OnPresence;
        connection.Message -= OnMessage;
        connection.ConnectionFailed -= OnConnectionFailed;
    }

    private void OnStreamStart(XmppConnection _, string __) => Connected?.Invoke(this, EventArgs.Empty);

    private void OnElement(XmppConnection _, ElementArgs e)
    {
        logger.LogVerbose("{0}\n{1}", e.IsInput ? "IN:" : "OUT:", e.Stanza);

        if (e.IsInput)
            ReadRawMessage?.Invoke(this, e.Stanza.ToString());
        else
            WriteRawMessage?.Invoke(this, e.Stanza.ToString());
        switch (e)
        {
            case { IsInput: true, Stanza.Name: { NamespaceName: SharpXMPP.Namespaces.Streams, LocalName: Elements.Error } }:
                StreamError?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnSignedIn(XmppConnection sender, SignedInArgs e)
    {
        IsAuthenticated = true;
        Authenticated?.Invoke(this, EventArgs.Empty);
        sender.Send(new XMPPPresence());
    }

    protected void OnPresence(XmppConnection? _, XMPPPresence presence) => Presence?.Invoke(this, presence.Wrap());

    private void OnMessage(XmppConnection _, XMPPMessage message)
    {
        var wrapped = message.Wrap();
        if (wrapped.Type == MessageType.GroupChat)
            RoomMessage?.Invoke(this, EventArgs.Empty);

        Message?.Invoke(this, message.Wrap());
    }

    private void OnConnectionFailed(XmppConnection _, ConnFailedArgs e)
    {
        // NOTE: ideally, we shouldn't log the exception here since we're able to propagate it further. But,
        // unfortunately, in such case we may lose e.Message (since only e.Exception gets propagated). So we have to log
        // e.Message here. And if we're doing this, then it would be odd to only log e.Message without e.Exception.
        if (e.Message != null)
            logger.LogError($"Connection failed: {e.Message}.", e.Exception);

        IsAuthenticated = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
        Error?.Invoke(this, e.Exception ?? new MessageOnlyException(e.Message));
    }

    public void Disconnect()
    {
        currentClient!.Close();
        currentClient.Dispose();
        currentClient = null;
    }

    public void SendElement(XmlElement element)
    {
        currentClient!.Send(XElement.Parse(element.ToString()!));
    }

    public void SendPresence(PresenceDetails presenceDetails)
    {
        var presence = new XMPPPresence();
        if (presenceDetails.Type != null)
            presence.SetAttributeValue("type", presenceDetails.Type.Value.Map());
        if (presenceDetails.To != null)
            presence.To = presenceDetails.To.Value.Map();

        if (presenceDetails.StatusText != null)
        {
            var status = presence.GetOrCreateChildElement(
                XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + "status");
            status.Value = presenceDetails.StatusText;
        }

        if (presenceDetails.StatusType != null)
        {
            var show = presence.GetOrCreateChildElement(XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + "show");
            show.Value = presenceDetails.StatusType.Value.Map();
        }

        if (presenceDetails.PhotoHash != null)
        {
            var x = presence.GetOrCreateChildElement(
                XNamespace.Get(Namespaces.VCardTempXUpdate) + Elements.X);
            var photo = x.GetOrCreateChildElement(
                XNamespace.Get(Namespaces.VCardTempXUpdate) + Elements.Photo);
            photo.Value = presenceDetails.PhotoHash;
        }

        if (presenceDetails.Priority != null)
        {
            var priority = presence.GetOrCreateChildElement(
                XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + "priority");
            priority.Value = presenceDetails.Priority.Value.ToString(CultureInfo.InvariantCulture);
        }

        currentClient!.Send(presence);
    }

    internal void SendPresence(XMPPPresence presence)
    {
        var from = presence.GetOrCreateAttribute(Attributes.From);
        if (string.IsNullOrEmpty(from.Value))
            from.Value = currentClient!.Jid.FullJid;

        currentClient!.Send(presence);
    }

    public void SendIq(IIq iq)
    {
        SendIq(iq.Unwrap()).NoAwait(logger);
    }

    internal void SendMessage(XMPPMessage message) => currentClient!.Send(message);

    public void SendMessage(MessageType type, Jid target, string body)
    {
        var message = new XMPPMessage
        {
            To = target.Map(),
            Text = body
        };
        if (type != MessageType.Normal)
            message.GetOrCreateAttribute(Attributes.Type).Value = type.Map();

        SendMessage(message);
    }

    public async Task<IIq> SendCaptchaAnswer(Jid roomId, string challenge, string answer)
    {
        var iq = new XMPPIq(XMPPIq.IqTypes.set)
        {
            To = roomId.Bare.Map()
        };

        var form = iq.GetOrCreateChildElement(XNamespace.Get(Namespaces.Captcha) + Elements.Captcha)
            .GetOrCreateChildElement(XNamespace.Get(Namespaces.Data) + Elements.X);
        form.SetAttributeValue(Attributes.Type, "submit");

        void AddField(string name, string value)
        {
            var field = new XElement(form.Name.Namespace + Elements.Field);
            field.SetAttributeValue(Attributes.Var, name);
            field.GetOrCreateChildElement(form.Name.Namespace + Elements.Value).Value = value;
            form.Add(field);
        }

        AddField("FORM_TYPE", Namespaces.Captcha);
        AddField("from", roomId.ToString());
        AddField("challenge", challenge);
        AddField("sid", "");
        AddField("ocr", answer);

        var response = await SendIq(iq, false);
        return response.Wrap();
    }

    private Task<XMPPIq> SendIq(XMPPIq iq, bool throwOnError = true)
    {
        var result = new TaskCompletionSource<XMPPIq>();
        currentClient!.Query(iq, response =>
        {
            try
            {
                if (throwOnError)
                {
                    var error = response.Element(XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + Elements.Error);
                    if (error != null)
                        throw new Exception("XMPP error: " + error);
                }

                result.SetResult(response);
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }

    public async Task<VCard?> GetVCard(Jid jid)
    {
        var iq = new XMPPIq(XMPPIq.IqTypes.get)
        {
            To = jid.Map()
        };
        iq.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCard);

        var response = await SendIq(iq, false).ConfigureAwait(false);
        var error = response.Element(XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + Elements.Error);
        if (error != null)
            return null;

        var vCardElement = response.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCard);
        var photoElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardPhoto);
        var fullNameElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardFullName);
        var emailElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardEmail)?
            .Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardEmailInternet);
        var birthDateElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardBDay);
        var nicknameElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardNickname);
        var descriptionElement = vCardElement?.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardDesc);

        var photo = photoElement == null ? null : ReadPhoto(photoElement);
        var birthDate =
            birthDateElement == null ? null
            : DateTime.TryParse(birthDateElement.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var bd) ? (DateTime?)bd : null;

        return new VCard
        {
            Photo = photo,
            FullName = fullNameElement?.Value,
            Email = emailElement?.Value,
            Birthday = birthDate,
            Nick = nicknameElement?.Value,
            Comments = descriptionElement?.Value
        };

        byte[]? ReadPhoto(XElement photoEl)
        {
            var binVal = photoEl.Element(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardPhotoBinVal);
            if (binVal == null) return null;
            try
            {
                var bytes = Convert.FromBase64String(binVal.Value);
                if (bytes.Length == 0) return null;
                return bytes;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error while processing avatar of user {iq.From}.", ex);
                return null;
            }
        }
    }

    public async Task<IIq> UpdateVCard(VCard vCard)
    {
        var iq = new XMPPIq(XMPPIq.IqTypes.set);
        var query = iq.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCard);

        if (vCard.Photo != null)
        {
            var binVal = query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardPhoto)
                .GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardPhotoBinVal);
            using var stream = new MemoryStream();
            Image.FromStream(new MemoryStream(vCard.Photo)).Save(stream, ImageFormat.Png);
            binVal.Value = Convert.ToBase64String(stream.ToArray());
        }

        if (vCard.FullName != null)
        {
            query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardFullName)
                .Value = vCard.FullName;
        }

        if (vCard.Email != null)
        {
            query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardEmail)
                .GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardEmailInternet)
                .Value = vCard.Email;
        }

        if (vCard.Birthday != null)
        {
            query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardBDay)
                .Value = vCard.Birthday.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        if (vCard.Nick != null)
        {
            query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardNickname)
                .Value = vCard.Nick;
        }

        if (vCard.Comments != null)
        {
            query.GetOrCreateChildElement(XNamespace.Get(Namespaces.VCardTemp) + Elements.VCardDesc)
                .Value = vCard.Comments;
        }

        var response = await SendIq(iq, false);
        return response.Wrap();
    }

    public async Task<ClientInfo?> GetClientInfo(Jid jid)
    {
        var iq = new XMPPIq(XMPPIq.IqTypes.get);
        iq.GetOrCreateChildElement(XNamespace.Get(Namespaces.Version) + Elements.Query);

        var response = await SendIq(iq, false);
        var error = response.Element(XNamespace.Get(SharpXMPP.Namespaces.JabberClient) + Elements.Error);
        if (error != null)
            return null;

        var query = response.Element(XNamespace.Get(Namespaces.Version) + Elements.Query);
        var name = query?.Element(XNamespace.Get(Namespaces.Version) + Elements.Name);
        var version = query?.Element(XNamespace.Get(Namespaces.Version) + Elements.Version);
        var os = query?.Element(XNamespace.Get(Namespaces.Version) + Elements.Os);

        return new ClientInfo(os?.Value, version?.Value, name?.Value);
    }

    public async Task<IDiscoNode?> DiscoverItems(Jid jid, string node)
    {
        var iq = new XMPPIq(XMPPIq.IqTypes.get)
        {
            To = jid.Map()
        };
        iq.GetOrCreateChildElement(XNamespace.Get(Namespaces.DiscoItems) + Elements.Query)
            .GetOrCreateAttribute(Attributes.Node).Value = node;

        var response = await SendIq(iq);

        var query = response.Element(XNamespace.Get(Namespaces.DiscoItems) + Elements.Query);
        if (query == null)
            throw new Exception("Cannot find query in the IQ response.");

        var items = query.Elements(XNamespace.Get(Namespaces.DiscoItems) + Elements.Item);
        return new DiscoNode(jid, node, null, items);
    }
}
