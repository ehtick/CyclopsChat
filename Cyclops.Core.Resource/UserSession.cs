using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cyclops.Configuration;
using Cyclops.Core.CustomEventArgs;
using Cyclops.Core.Helpers;
using Cyclops.Core.Resources;
using Cyclops.Core.Security;
using Cyclops.Xmpp.Client;
using Cyclops.Xmpp.Data;
using Cyclops.Xmpp.Data.Rooms;
using Cyclops.Xmpp.Protocol;

namespace Cyclops.Core.Resource;

public class UserSession : NotifyPropertyChangedBase, IUserSession
{
    private readonly ILogger logger;
    private readonly Action<Action> dispatcher;
    private readonly IXmppDataExtractor dataExtractor;

    /// <summary>
    /// Timer for reconnect
    /// </summary>
    private readonly Timer reconnectTimer;
    private readonly IStringEncryptor stringEncryptor;
    private readonly IChatObjectsValidator commonValidator;
    private ConnectionConfig connectionConfig;
    private Jid currentUserId;
    private StatusType statusType;
    private string status;
    private bool isAuthenticated;
    private bool isAuthenticating;

    private IObservableCollection<IConferenceMessage> privateMessages;

    public UserSession(
        ILogger logger,
        Action<Action> dispatcher,
        IXmppDataExtractor dataExtractor,
        IStringEncryptor stringEncryptor,
        IChatObjectsValidator commonValidator,
        IXmppClient xmppClient)
    {
        this.logger = logger;
        this.dispatcher = dispatcher;
        this.dataExtractor = dataExtractor;
        this.stringEncryptor = stringEncryptor;
        this.commonValidator = commonValidator;
        XmppClient = xmppClient;

        AutoReconnect = true;
        Conferences = new InternalObservableCollection<IConference>();
        PrivateMessages = new InternalObservableCollection<IConferenceMessage>();

        reconnectTimer = new Timer(ReconnectTimerTick, null, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(10));
        SubscribeToEvents();

        statusType = StatusType.Online;
        status = "CyclopsChat " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
    }

    public IXmppClient XmppClient { get; }

    #region IUserSession Members

    public void Dispose()
    {
        Close();
    }

    public IObservableCollection<IConferenceMessage> PrivateMessages
    {
        get { return privateMessages; }
        set
        {
            privateMessages = value;
            OnPropertyChanged("PrivateMessages");
        }
    }

    public bool AutoReconnect { get; set; }

    public string Status
    {
        get
        {
            return status;
        }
        set
        {
            status = value;
            ChangeStatus(StatusType, value);
            OnPropertyChanged("Status");
        }
    }

    public StatusType StatusType
    {
        get { return statusType; }
        set
        {
            statusType = value;
            ChangeStatus(value, Status);
            OnPropertyChanged("StatusType");
        }
    }

    public bool IsAuthenticating
    {
        get { return isAuthenticated; }
        set
        {
            isAuthenticated = value;
            OnPropertyChanged("IsAuthenticated");
        }
    }

    public bool IsAuthenticated
    {
        get { return isAuthenticating; }
        set
        {
            isAuthenticating = value;
            OnPropertyChanged("IsAuthenticated");
        }
    }

    public Jid CurrentUserId
    {
        get { return currentUserId; }
        set
        {
            currentUserId = value;
            OnPropertyChanged("CurrentUserId");
        }
    }


    public ConnectionConfig ConnectionConfig
    {
        get { return connectionConfig; }
        set
        {
            connectionConfig = value;
            OnPropertyChanged("ConnectionConfig");
        }
    }

    public IObservableCollection<IConference> Conferences { get; private set; }

    public void SendPresence(PresenceDetails presenceDetails) => XmppClient.SendPresence(presenceDetails);

    public void SendIq(IIq iq) => XmppClient.SendIq(iq);

    public void ChangeStatus(StatusType type, string status)
    {
        var conferenceManager = XmppClient.ConferenceManager;
        conferenceManager.Status = status;
        conferenceManager.StatusType = type;

        if (!IsAuthenticated || !XmppClient.IsAuthenticated)
            return;

        XmppClient.SendPresence(new PresenceDetails
        {
            Type = PresenceType.Available,
            StatusType = type,
            StatusText = status,
            Priority = 30
        });
    }

    public void OpenConference(Jid id)
    {
        Conferences.AsInternalImpl().Add(new Conference(logger, dispatcher, this, dataExtractor, id));
    }

    public void AuthenticateAsync(ConnectionConfig info)
    {
        if (!commonValidator.ValidateConfig(info))
            return;

        var resource = "cyclops_v." + Assembly.GetAssembly(GetType()).GetName().Version.ToString(3);
        currentUserId = new Jid(info.User ?? "", info.Server ?? "", resource);

        IsAuthenticated = true;
        ConnectionConfig = info;

        XmppClient.Connect(
            server: info.Server,
            host: info.NetworkHost,
            user: info.User,
            password: stringEncryptor.DecryptString(info.EncodedPassword),
            port: info.Port,
            resource: resource
        );
    }

    public void Close()
    {
        try
        {
            XmppClient.Disconnect();
        }
        catch(Exception ex)
        {
            logger.LogError("Error during session close.", ex);
        }

        XmppClient.Dispose();
    }

    public void Reconnect()
    {
        if (ConnectionConfig == null)
            throw new InvalidOperationException(
                "You can't call Reconnect() before at least one AuthenticateAsync() calling");

        if (AutoReconnect)
            AuthenticateAsync(ConnectionConfig);
    }

    public void SendPrivate(Jid target, string body) =>
        XmppClient.SendMessage(MessageType.Chat, target, body);

    /// <summary>
    /// Raised when authentication complete (success or not)
    /// </summary>
    public event EventHandler<AuthenticationEventArgs> Authenticated = delegate { };

    /// <summary>
    /// Raised when authentication complete (success or not)
    /// </summary>
    public event EventHandler<AuthenticationEventArgs> ConnectionDropped = delegate { };

    public event EventHandler<IPresence>? Presence;

    public event EventHandler PublicMessage = delegate { };
    public event EventHandler<ErrorEventArgs> ErrorMessageRecieved = delegate { };

    public void RemoveFromBookmarks(Jid conferenceId) => XmppClient.BookmarkManager.RemoveBookmark(conferenceId.Bare);

    public void AddToBookmarks(Jid conferenceId) =>
        XmppClient.BookmarkManager.AddBookmark(conferenceId.Bare, conferenceId.Local, true, conferenceId.Resource);

    #endregion

    #region JabberClient events

    private void OnStreamError(object _, object __)
    {
        dispatcher(() =>
        {
            if (IsAuthenticated)
            {
                IsAuthenticated = false;
                Authenticated(this, new AuthenticationEventArgs(
                    ConnectionErrorKind.UnknownError,
                    ErrorMessageResources.CommonAuthenticationErrorMessage));
            }
            else
                ConnectionDropped(this, new AuthenticationEventArgs(
                    ConnectionErrorKind.ConnectionError,
                    ErrorMessageResources.CommonAuthenticationErrorMessage));

            reconnectTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10));
        });
    }

    private void OnError(object sender, Exception ex)
    {
        logger.LogError("Error from the XMPP client.", ex);
        dispatcher(() =>
        {
            if (IsAuthenticated)
                IsAuthenticated = false;
            ConnectionDropped(this, new AuthenticationEventArgs(ConnectionErrorKind.ConnectionError, ex.Message));
            reconnectTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10));
        });
    }

    private void OnDisconnected(object _, object __)
    {
        dispatcher(() => reconnectTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10)));
    }

    private void OnAuthenticationError(object _, object __)
    {
        dispatcher(() =>
            Authenticated(this,
                new AuthenticationEventArgs(
                    ConnectionErrorKind.InvalidPasswordOrUserName,
                    ErrorMessageResources.InvalidLoginOrPasswordMessage)));
    }

    private bool firstAuthentigation = true;

    private void OnAuthenticated(object _, object __)
    {
        dispatcher(() =>
        {
            IsAuthenticated = true;
            Authenticated(this, new AuthenticationEventArgs());

            if (firstAuthentigation)
            {
                XmppClient.BookmarkManager.RequestBookmarks();
                firstAuthentigation = false;
            }

            //TODO: remove this shit:)
            OnPropertyChanged("Status");
            OnPropertyChanged("StatusType");
        });
    }

    #endregion

    private void SubscribeToEvents()
    {
        XmppClient.Presence += (_, presence) => dispatcher(() => Presence?.Invoke(this, presence));
        XmppClient.RoomMessage += (_, _) => dispatcher(() => PublicMessage.Invoke(this, EventArgs.Empty));

        XmppClient.IqQueryManager.TimeQueried += (_, iq) => IqCommonHandler.HandleTime(this, iq);
        XmppClient.IqQueryManager.LastActivityQueried += (_, iq) => IqCommonHandler.HandleLast(this, iq);
        XmppClient.IqQueryManager.VersionQueried += (_, iq) => IqCommonHandler.HandleVersion(this, iq);

        XmppClient.Authenticated += OnAuthenticated;
        XmppClient.AuthenticationError += OnAuthenticationError;
        XmppClient.Disconnected += OnDisconnected;

        XmppClient.Error += OnError;
        XmppClient.StreamError += OnStreamError;

        XmppClient.BookmarkManager.BookmarkAdded += OnBookmarkAdded;

        XmppClient.Message += OnMessage;
    }

    private void OnBookmarkAdded(object _, IBookmark bookmark)
    {
        dispatcher(() =>
        {
            var conferenceJid = bookmark.ConferenceJid.WithResource(bookmark.Nick);
            if (bookmark.AutoJoin)
                OpenConference(conferenceJid);
        });
    }

    public Task<IIq> SendCaptchaAnswer(Jid mucId, string challenge, string answer) =>
        XmppClient.SendCaptchaAnswer(mucId, challenge, answer);

    private void OnMessage(object sender, IMessage msg)
    {
        dispatcher(() =>
        {
            //some conferences are not allowed to send privates
            if (msg.Error != null)
            {
                ErrorMessageRecieved(this, new ErrorEventArgs(msg.From, msg.Error.Message));
                return;
            }

            if (msg.Type != Xmpp.Protocol.MessageType.Chat)
                return;

            PrivateMessages.AsInternalImpl().Add(new PrivateMessage
            {
                AuthorId = msg.From.Value,
                AuthorNick = msg.From.Value.Resource,
                Body = msg.Body,
            });
        });
    }

    public void RefreshConferenceList(string? service)
    {
        if (service == null)
        {
            ConferencesListReceived(null, new ConferencesListEventArgs(new List<Tuple<Jid, string>>()));
            return;
        }

        async Task DoRefresh()
        {
            var node = await XmppClient.DiscoverItems(Jid.Parse(service), Namespaces.Muc);
            if (node == null)
            {
                ConferencesListReceived(null, new ConferencesListEventArgs(new List<Tuple<Jid, string>>()));
                return;
            }

            ConferenceServiceId = node.Jid!.Value;

            var result = node.Children.Select(dn => new Tuple<Jid, string>(dn.Jid!.Value, dn.Name)).ToList();

            ConferencesListReceived(null, new ConferencesListEventArgs(result));
        }

        DoRefresh().NoAwait(logger);
    }

    public void StartPrivate(Jid conferenceUserId)
    {
        PrivateMessages.AsInternalImpl().Add(new PrivateMessage {AuthorId = conferenceUserId});
    }

    public Task<ClientInfo?> GetClientInfo(Jid jid) => XmppClient.GetClientInfo(jid);

    public Task<VCard?> GetVCard(Jid target) => XmppClient.GetVCard(target);

    public async Task UpdateVCard(VCard vCard)
    {
        logger.LogInfo("Updating vCard.");
        var updateResult = await XmppClient.UpdateVCard(vCard);
        if (updateResult.Error != null)
        {
            logger.LogInfo($"Update vCard error: {updateResult.Error}.");
            return;
        }

        var photoHash = vCard.Photo == null ? "" : ImageUtils.CalculateSha1HashOfAnImage(vCard.Photo);
        XmppClient.SendPresence(new PresenceDetails
        {
            PhotoHash = photoHash
        });
    }

    public Jid ConferenceServiceId { get; private set; }

    public void RaiseBookmarksReceived()
    {
        var data = XmppClient.BookmarkManager.Bookmarks
            .Select(i => new Tuple<Jid, string>(i.ConferenceJid.WithResource(i.Nick), i.Name)).ToList();
        ConferencesListReceived(null, new ConferencesListEventArgs(data));
    }

    public event EventHandler<ConferencesListEventArgs> ConferencesListReceived = delegate { };

    private void ReconnectTimerTick(object sender)
    {
        reconnectTimer.Change(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(10));
        Reconnect();
    }

    public IRoom GetRoom(Jid roomId) => XmppClient.ConferenceManager.GetRoom(roomId);
}
