using System.Windows;
using Cyclops.MainApplication.Notifications;
using Cyclops.MainApplication.View;
using GalaSoft.MvvmLight.CommandWpf;

namespace Cyclops.MainApplication.ViewModel;

partial class MainViewModel
{
    public RelayCommand OpenConferenceList { get; private set; }
    public RelayCommand CloseActiveConferenceOrPrivate { get; private set; }
    public RelayCommand Quit { get; private set; }
    public RelayCommand ShowOrHide { get; private set; }

    private void InitializeCommands()
    {
        OpenConferenceList = new RelayCommand(OpenConferenceListAction, OpenConferenceListCanExecute);
        CloseActiveConferenceOrPrivate = new RelayCommand(CloseActiveConferenceOrPrivateAction, CloseActiveConferenceOrPrivateCanExecute);
        Quit = new RelayCommand(() => App.Current.Shutdown());
        ShowOrHide = new RelayCommand(ShowOrHideAction);
    }

    private void ShowOrHideAction()
    {
        TrayController.HideOrShowWindow(Application.Current.MainWindow);
    }

    private void CloseActiveConferenceOrPrivateAction()
    {
        if (SelectedConference != null)
            SelectedConference.Conference.LeaveAndClose();
        if (SelectedPrivate != null)
            PrivateViewModels.Remove(SelectedPrivate);
    }

    private bool CloseActiveConferenceOrPrivateCanExecute()
    {
        return SelectedConference != null || SelectedPrivate != null;
    }

    private static void OpenConferenceListAction()
    {
        //TODO: implemnt cache
        var dlg = new ConferencesList();
        dlg.Owner = Application.Current.MainWindow;
        dlg.ShowDialog();
    }

    private bool OpenConferenceListCanExecute()
    {
        return Session.IsAuthenticated;
    }
}
