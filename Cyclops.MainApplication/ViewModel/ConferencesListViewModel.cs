﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Cyclops.Core;
using Cyclops.Core.CustomEventArgs;
using Cyclops.Core.Resource;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;

namespace Cyclops.MainApplication.ViewModel
{
    public class ConferencesListViewModel : ViewModelBase
    {
        private IEnumerable<ConferenceInfo> conferences;
        private string filter;
        private bool isBusy;
        private string openWithNick;
        private ConferenceInfo selectedConference;
        private IEnumerable<ConferenceInfo> sourceConferences;

        public ConferencesListViewModel()
        {
            OpenConference = new RelayCommand(OpenConferenceAction, OpenConferenceCanExecute);

            if (IsInDesignMode)
            {
                Conferences = new[] 
                                  {
                                      new ConferenceInfo {Id = new FakeId {User = "cyclops"}, Name = "Cyclops development test"},
                                      new ConferenceInfo {Id = new FakeId {User = "main"}, Name = "Main (5)"},
                                      new ConferenceInfo {Id = new FakeId {User = "anime"}, Name = "Anime"},
                                  };
                return;
            }

            IsBusy = true;
            IUserSession session = ChatObjectFactory.GetSession();
            OpenWithNick = session.CurrentUserId.User;
            session.ConferencesListReceived += ConferencesListReceived;
            session.GetConferenceListAsync();
        }

        public bool IsBusy
        {
            get { return isBusy; }
            set
            {
                isBusy = value;
                RaisePropertyChanged("IsBusy");
            }
        }

        public IEnumerable<ConferenceInfo> Conferences
        {
            get { return conferences; }
            set
            {
                conferences = value;
                RaisePropertyChanged("Conferences");
            }
        }

        public ConferenceInfo SelectedConference
        {
            get { return selectedConference; }
            set
            {
                selectedConference = value;
                RaisePropertyChanged("SelectedConference");
                if (value != null)
                    value.Name = conferenceCache[value.Id]();
            }
        }

        public string Filter
        {
            get { return filter; }
            set
            {
                filter = value;
                RaisePropertyChanged("Filter");
                if (string.IsNullOrEmpty(value))
                    Conferences = sourceConferences.ToArray();
                else
                    Conferences = sourceConferences.Where(i => 
                        i.Name.ToLower().Contains(value.ToLower()) ||
                        i.Id.User.ToLower().Contains(value.ToLower())).ToArray();
            }
        }

        public string OpenWithNick
        {
            get { return openWithNick; }
            set
            {
                openWithNick = value;
                RaisePropertyChanged("OpenWithNick");
            }
        }

        public RelayCommand OpenConference { get; set; }

        private Dictionary<IEntityIdentifier, Func<string>> conferenceCache =
            new Dictionary<IEntityIdentifier, Func<string>>();

        private void ConferencesListReceived(object sender, ConferencesListEventArgs e)
        {
            IsBusy = false;
            if (!e.Success)
                return;
            sourceConferences = Conferences = e.Result.Select(i => 
                new ConferenceInfo {Id = i.Item1, Name = "Select item to get description"}).OrderBy(i => i.Id.User).ToList();
            conferenceCache = e.Result.ToDictionary(i => i.Item1, i => (Func<string>)(() => i.Item2));
        }

        private bool OpenConferenceCanExecute()
        {
            return SelectedConference != null;
        }

        private void OpenConferenceAction()
        {
            if (SelectedConference == null)
                return;

            IUserSession session = ChatObjectFactory.GetSession();

            IConference existsConference = session.Conferences.FirstOrDefault(i => IsEqual(i.ConferenceId, SelectedConference.Id));
            if (existsConference != null)
            {
                if (existsConference.IsInConference)
                {
                    MessageBox.Show("You are already in this room.", "Warrning", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                existsConference.LeaveAndClose();
            }

            string nick = string.IsNullOrWhiteSpace(OpenWithNick) ? session.CurrentUserId.User : OpenWithNick;
            session.OpenConference(IdentifierBuilder.Create(SelectedConference.Id.User, SelectedConference.Id.Server, nick));
            Close(this, EventArgs.Empty);
        }

        public event EventHandler Close = delegate { };

        private static bool IsEqual(IEntityIdentifier id1, IEntityIdentifier id2)
        {
            return string.Equals(id1.User, id2.User, StringComparison.InvariantCultureIgnoreCase) &&
                   string.Equals(id1.Server, id2.Server, StringComparison.InvariantCultureIgnoreCase);
        }
    }

    public class ConferenceInfo : NotifyPropertyChangedBase
    {
        private IEntityIdentifier id;
        public IEntityIdentifier Id
        {
            get { return id; }
            set
            {
                id = value;
                OnPropertyChanged("Id");
            }
        }

        private string name;
        public string Name
        {
            get { return name; }
            set
            {
                name = value;
                OnPropertyChanged("Name");
            }
        }
    }

    public class FakeId : IEntityIdentifier
    {
        #region IEntityIdentifier Members

        public string Server { get; set; }
        public string User { get; set; }
        public string Resource { get; set; }

        #endregion
    }
}