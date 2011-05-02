﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Threading;
using System.Xml;
using Cyclops.Core.Configuration;
using Cyclops.Core.CustomEventArgs;
using Cyclops.Core.Resources;
using Cyclops.Core.Security;
using jabber;
using jabber.client;
using jabber.connection;
using jabber.protocol;
using jabber.protocol.client;
using jabber.protocol.stream;

namespace Cyclops.Core.Resource
{
    public class UserSession : NotifyPropertyChangedBase, IUserSession
    {
        /// <summary>
        /// Timer for reconnect
        /// </summary>
        private readonly DispatcherTimer reconnectTimer;
        private readonly IStringEncryptor stringEncryptor;
        private readonly IChatObjectsValidator commonValidator;
        private ConnectionConfig connectionConfig;
        private IEntityIdentifier currentUserId;
        private string status;
        private bool isAuthenticated;
        private bool isAuthenticating;

        private IObservableCollection<IConferenceMessage> privateMessages;

        public UserSession(IStringEncryptor stringEncryptor, IChatObjectsValidator commonValidator, Dispatcher dispatcher)
        {
            AutoReconnect = true;
            Dispatcher = dispatcher;
            Conferences = new InternalObservableCollection<IConference>();
            PrivateMessages = new InternalObservableCollection<IConferenceMessage>();
            this.stringEncryptor = stringEncryptor;
            this.commonValidator = commonValidator;
            JabberClient = new JabberClient();
            ConferenceManager = new ConferenceManager {Stream = JabberClient};
            DiscoManager = new DiscoManager {Stream = JabberClient};
            reconnectTimer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(10)};

            SubscribeToEvents();
        }

        internal JabberClient JabberClient { get; set; }
        internal ConferenceManager ConferenceManager { get; set; }
        internal DiscoManager DiscoManager { get; set; }

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
            get { return status; }
            set
            {
                status = value;
                ChangeStatus(StatusType.Online, value);
                OnPropertyChanged("Status");
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


        public IEntityIdentifier CurrentUserId
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

        public void ChangeStatus(StatusType type, string status)
        {
            if (!IsAuthenticated)
                return;
            JabberClient.Presence(PresenceType.available, status, type.StatusTypeToString(), 30);
        }

        public void OpenConference(IEntityIdentifier id)
        {
            Conferences.AsInternalImpl().Add(new Conference(this, id));
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
            JabberClient.Resource = "cyclops v." + Assembly.GetAssembly(GetType()).GetName().Version.ToString(3);

            if (Dispatcher != null)
                JabberClient.InvokeControl = new SynchronizeInvokeImpl(Dispatcher);

            currentUserId = new JID(info.User, info.Server, JabberClient.Resource);
            ConnectionConfig = info;

            // some default settings
            JabberClient.AutoReconnect = -1;
            JabberClient.AutoPresence = false;
            JabberClient.AutoRoster = false; 
            JabberClient.Priority = -1;
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
        }

        public void Reconnect()
        {
            if (ConnectionConfig == null)
                throw new InvalidOperationException(
                    "You can't call Reconnect() before at least one AuthenticateAsync() calling");

            if (AutoReconnect)
                AuthenticateAsync(ConnectionConfig);
        }

        public void SendPrivate(IEntityIdentifier target, string body)
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

        public event EventHandler PublicMessage = delegate { };

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

        private void jabberClient_OnAuthenticate(object sender)
        {
            IsAuthenticated = true;
            Authenticated(sender, new AuthenticationEventArgs());

            Status = "cyclopschat.codeplex.com " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        }

        #endregion

        private void SubscribeToEvents()
        {
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

            ConferenceManager.OnRoomMessage += ConferenceManager_OnRoomMessage;
            ConferenceManager.OnJoin += ConferenceManager_OnJoin;

            JabberClient.OnMessage += JabberClient_OnMessage;
            reconnectTimer.Tick += ReconnectTimerTick;
        }

        void ConferenceManager_OnJoin(Room room)
        {
            //presence flood
            //ChangeStatus(StatusType.Online, Status);//retranslate status to conference
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
            //throw new NotImplementedException();
        }

        private void JabberClient_OnMessage(object sender, Message msg)
        {
            if (msg.Type != MessageType.chat)
                return;

            PrivateMessages.AsInternalImpl().Add(new PrivateMessage
                                                     {
                                                         AuthorId = msg.From,
                                                         AuthorNick = msg.From.Resource,
                                                         Body = msg.Body,
                                                     });
        }

        private void ConferenceManager_OnRoomMessage(object sender, Message msg)
        {
            PublicMessage(this, EventArgs.Empty);
        }

        public void GetConferenceListAsync()
        {
            DiscoManager.BeginFindServiceWithFeature(URI.MUC, DiscoHandlerFindServiceWithFeature, new object());
        }

        public void StartPrivate(IEntityIdentifier conferenceUserId)
        {
            PrivateMessages.AsInternalImpl().Add(new PrivateMessage {AuthorId = conferenceUserId});
        }

        void DiscoHandlerFindServiceWithFeature(DiscoManager sender, DiscoNode node, object state)
        {
            DiscoManager.BeginGetItems(node, DiscoHandlerGetItems, state);
        }
        
        void DiscoHandlerGetItems(DiscoManager sender, DiscoNode node, object state)
        {
            var result = (from DiscoNode childNode in node.Children
                            select new Tuple<IEntityIdentifier, string>(childNode.JID, childNode.Name)).ToList();
            ConferencesListReceived(null, new ConferencesListEventArgs(result));
        }

        public event EventHandler<ConferencesListEventArgs> ConferencesListReceived = delegate { }; 
        
        private void ReconnectTimerTick(object sender, EventArgs e)
        {
            reconnectTimer.Stop();
            Reconnect();
        }
    }
}