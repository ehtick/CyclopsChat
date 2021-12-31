﻿using System;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml;
using Cyclops.Configuration;
using Cyclops.Core.CustomEventArgs;
using Cyclops.Core.Resources;
using Cyclops.Core.Security;
using Cyclops.Xmpp.Client;
using Cyclops.Xmpp.Data;
using Cyclops.Xmpp.Data.Rooms;
using Cyclops.Xmpp.JabberNet.Client;
using Cyclops.Xmpp.JabberNet.Protocol;
using Cyclops.Xmpp.Protocol;
using jabber;
using jabber.client;
using jabber.connection;
using jabber.protocol;
using jabber.protocol.client;
using jabber.protocol.iq;
using jabber.protocol.stream;
using VCard = Cyclops.Xmpp.Data.VCard;

namespace Cyclops.Core.Resource
{
    public class UserSession : NotifyPropertyChangedBase, IUserSession
    {
        private readonly ILogger logger;
        private readonly IXmppDataExtractor dataExtractor;
        private readonly JabberNetXmppClient xmppClient;

        /// <summary>
        /// Timer for reconnect
        /// </summary>
        private readonly DispatcherTimer reconnectTimer;
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
            IXmppDataExtractor dataExtractor,
            IStringEncryptor stringEncryptor,
            IChatObjectsValidator commonValidator,
            Dispatcher dispatcher)
        {
            this.logger = logger;
            this.dataExtractor = dataExtractor;

            AutoReconnect = true;
            Dispatcher = dispatcher;
            Conferences = new InternalObservableCollection<IConference>();
            PrivateMessages = new InternalObservableCollection<IConferenceMessage>();
            this.stringEncryptor = stringEncryptor;
            this.commonValidator = commonValidator;
            JabberClient = new JabberClient();
            ConferenceManager = new ConferenceManager {Stream = JabberClient};
            xmppClient = new JabberNetXmppClient(JabberClient, ConferenceManager);
            BookmarkManager = new BookmarkManager {Stream = JabberClient, AutoPrivate = false, ConferenceManager = ConferenceManager };

            DiscoManager = new DiscoManager {Stream = JabberClient};
            reconnectTimer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(10)};
            SubscribeToEvents();

            statusType = StatusType.Online;
            status = "CyclopsChat " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        }

        public IXmppClient XmppClient => xmppClient;
        public JabberClient JabberClient { get; set; }
        internal ConferenceManager ConferenceManager { get; set; }
        internal DiscoManager DiscoManager { get; set; }
        internal BookmarkManager BookmarkManager { get; set; }

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

        internal Dispatcher Dispatcher { get; set; }


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

        public void ChangeStatus(StatusType type, string status)
        {
            if (!IsAuthenticated || !JabberClient.IsAuthenticated)
                return;
            JabberClient.Presence(PresenceType.available, status, type.Map(), 30);
        }

        public void OpenConference(Jid id)
        {
            Conferences.AsInternalImpl().Add(new Conference(logger, this, dataExtractor, id));
        }

        public void AuthenticateAsync(ConnectionConfig info)
        {
            if (!commonValidator.ValidateConfig(info))
                return;

            JabberClient.Server = info.Server;
            JabberClient.NetworkHost = info.NetworkHost;
            JabberClient.User = info.User;
            JabberClient.Password = stringEncryptor.DecryptString(info.EncodedPassword);
            JabberClient.Port = info.Port;
            JabberClient.Resource = "cyclops_v." + Assembly.GetAssembly(GetType()).GetName().Version.ToString(3);

            if (Dispatcher != null)
                JabberClient.InvokeControl = new SynchronizeInvokeImpl(Dispatcher);

            currentUserId = new Jid(info.User ?? "", info.Server ?? "", JabberClient.Resource);
            ConnectionConfig = info;

            // some default settings
            JabberClient.AutoReconnect = -1;
            JabberClient.AutoPresence = true;

            bool isVkServer = info.Server.Equals("vk.com", StringComparison.InvariantCultureIgnoreCase);
            if (isVkServer)
            {
                JabberClient.AutoRoster = true;
            }
            else
            {
                JabberClient.AutoRoster = false;
                JabberClient.Priority = 0;// -1;
            }

            JabberClient[Options.SASL_MECHANISMS] = MechanismType.DIGEST_MD5;
            JabberClient.KeepAlive = 20F;

            //let's go!
            IsAuthenticated = true;
            JabberClient.Connect();
        }

        public void Close()
        {
            try
            {
                JabberClient.Close(true);
            }
            catch
            {
            }

            xmppClient.Dispose();
        }

        public void Reconnect()
        {
            if (ConnectionConfig == null)
                throw new InvalidOperationException(
                    "You can't call Reconnect() before at least one AuthenticateAsync() calling");

            if (AutoReconnect)
                AuthenticateAsync(ConnectionConfig);
        }

        public void SendPrivate(Jid target, string body)
        {
            JabberClient.Message(MessageType.chat, target.ToString(), body);
        }

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

        public void RemoveFromBookmarks(Jid conferenceId)
        {
            BookmarkManager[conferenceId.Bare.Map()] = null;
        }

        public void AddToBookmarks(Jid conferenceId)
        {
            BookmarkManager.AddConference(conferenceId.Bare.Map(), conferenceId.User, true, conferenceId.Resource);
        }

        #endregion

        #region JabberClient events

        private void jabberClient_OnStreamError(object sender, XmlElement rp)
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
            reconnectTimer.Start();
        }

        private bool jabberClient_OnInvalidCertificate(object sender, X509Certificate certificate, X509Chain chain,
                                                       SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private void jabberClient_OnError(object sender, Exception ex)
        {
            if (IsAuthenticated)
                IsAuthenticated = false;
            ConnectionDropped(this, new AuthenticationEventArgs(ConnectionErrorKind.ConnectionError, ex.Message));
            reconnectTimer.Start();
        }

        private void jabberClient_OnDisconnect(object sender)
        {
            reconnectTimer.Start();
        }

        private void jabberClient_OnConnect(object sender, StanzaStream stream)
        {
        }

        private void jabberClient_OnAuthError(object sender, XmlElement rp)
        {
            Authenticated(sender,
                          new AuthenticationEventArgs(ConnectionErrorKind.InvalidPasswordOrUserName,
                                                      ErrorMessageResources.InvalidLoginOrPasswordMessage));
        }

        private bool firstAuthentigation = true;

        private void jabberClient_OnAuthenticate(object sender)
        {
            IsAuthenticated = true;
            Authenticated(sender, new AuthenticationEventArgs());

            if (firstAuthentigation)
            {
                BookmarkManager.RequestBookmarks();
                firstAuthentigation = false;
            }

            //TODO: remove this shit:)
            OnPropertyChanged("Status");
            OnPropertyChanged("StatusType");
        }

        #endregion

        private void SubscribeToEvents()
        {
            XmppClient.Presence += (_, presence) => Presence?.Invoke(this, presence);

            JabberClient.OnAuthenticate += jabberClient_OnAuthenticate;
            JabberClient.OnAuthError += jabberClient_OnAuthError;
            JabberClient.OnConnect += jabberClient_OnConnect;
            JabberClient.OnDisconnect += jabberClient_OnDisconnect;

            JabberClient.OnError += jabberClient_OnError;
            JabberClient.OnInvalidCertificate += jabberClient_OnInvalidCertificate;
            JabberClient.OnStreamError += jabberClient_OnStreamError;
            JabberClient.OnIQ += JabberClient_OnIQ;
            JabberClient.OnWriteText += JabberClient_OnWriteText;
            JabberClient.OnReadText += JabberClient_OnReadText;

            ConferenceManager.OnJoin += ConferenceManager_OnJoin;
            ConferenceManager.OnRoomMessage += ConferenceManager_OnRoomMessage;
            ConferenceManager.BeforeRoomPresenceOut += ConferenceManager_BeforeRoomPresenceOut;

            BookmarkManager.OnConferenceAdd += BookmarkManager_OnConferenceAdd;

            JabberClient.OnMessage += JabberClient_OnMessage;
            reconnectTimer.Tick += ReconnectTimerTick;
        }

        void ConferenceManager_OnJoin(Room room)
        {
        }

        void BookmarkManager_OnConferenceAdd(BookmarkManager manager, BookmarkConference conference)
        {
            JID conferenceJid = conference.JID;
            conferenceJid.Resource = conference.Nick;
            if (conference.AutoJoin)
                OpenConference(conferenceJid.Map());
            conference.AutoJoin = false;
        }

        void ConferenceManager_BeforeRoomPresenceOut(object sender, RoomPresenceEventArgs e)
        {
            RoomPresence pres = e.RoomPresence;
            pres.Status = Status;
            pres.Show = StatusType.Map();
        }

        //DEBUG:
        void JabberClient_OnReadText(object sender, string txt)
        {
        }

        //DEBUG:
        void JabberClient_OnWriteText(object sender, string txt)
        {
        }

        void JabberClient_OnIQ(object sender, IQ iq)
        {
            IqCommonHandler.Handle(JabberClient, iq);
        }

        public Task<IIq> SendCaptchaAnswer(Jid mucId, string challenge, string answer) =>
            XmppClient.SendCaptchaAnswer(mucId, challenge, answer);

        private void JabberClient_OnMessage(object sender, Message msg)
        {
            //some conferences are not allowed to send privates
            if (msg.Error != null)
            {
                ErrorMessageRecieved(this, new ErrorEventArgs(msg.From.Map(), msg.Error.Message));
                return;
            }

            if (msg.Type != MessageType.chat)
                return;

            PrivateMessages.AsInternalImpl().Add(new PrivateMessage
                                                     {
                                                         AuthorId = msg.From.Map(),
                                                         AuthorNick = msg.From.Resource,
                                                         Body = msg.Body,
                                                     });
        }

        private void ConferenceManager_OnRoomMessage(object sender, Message msg)
        {
            PublicMessage(this, EventArgs.Empty);
        }

        public void GetConferenceListAsync(string service = null)
        {
            if (!string.IsNullOrEmpty(service))
                DiscoManager.BeginGetItems(new DiscoNode(new JID(service), URI.MUC), DiscoHandlerFindServiceWithFeature, new object());
            else
                DiscoManager.BeginFindServiceWithFeature(URI.MUC, DiscoHandlerFindServiceWithFeature, new object());
        }

        public void StartPrivate(Jid conferenceUserId)
        {
            PrivateMessages.AsInternalImpl().Add(new PrivateMessage {AuthorId = conferenceUserId});
        }

        public Task<ClientInfo?> GetClientInfo(Jid jid) => XmppClient.GetClientInfo(jid);

        public Task<VCard> GetVCard(Jid target) => XmppClient.GetVCard(target);

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

        void DiscoHandlerFindServiceWithFeature(DiscoManager sender, DiscoNode node, object state)
        {
            if (node != null)
                ConferenceServiceId = node.JID.Map();
            DiscoManager.BeginGetItems(node, DiscoHandlerGetItems, state);
        }

        void DiscoHandlerGetItems(DiscoManager sender, DiscoNode node, object state)
        {
            var result = (from DiscoNode childNode in node.Children
                            select new Tuple<Jid, string>(childNode.JID.Map(), childNode.Name)).ToList();
            ConferencesListReceived(null, new ConferencesListEventArgs(result));
        }

        public void RaiseBookmarksReceived()
        {
            ConferencesListReceived(null, new ConferencesListEventArgs(BookmarkManager.Bookmarks.Select(i => new Tuple<Jid, string>(IdentifierBuilder.WithAnotherResource(i.Key.Map(), i.Value.Nick), i.Value.Value)).ToList()));
        }

        public event EventHandler<ConferencesListEventArgs> ConferencesListReceived = delegate { };

        private void ReconnectTimerTick(object sender, EventArgs e)
        {
            reconnectTimer.Stop();
            Reconnect();
        }

        public IRoom GetRoom(Jid roomId) => XmppClient.GetRoom(roomId);
    }
}
