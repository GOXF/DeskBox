using DeskBox.Services;

namespace DeskBox;

/// <summary>Fork feature 「映射整理」: host lifecycle (start after widget restore, always restore icons on exit).</summary>
public partial class App
{
    private MappedOrganizeService? _mappedOrganizeService;

    private void InitializeMappedOrganize(WidgetManager widgetManager)
    {
        if (_mappedOrganizeService is not null)
        {
            return;
        }

        try
        {
            var service = new MappedOrganizeService(
                SettingsService,
                widgetManager,
                LocalizationService,
                UiDispatcherQueue);
            _mappedOrganizeService = service;
            MappedOrganizeService.Current = service;
            service.Initialize();
            // Last-chance restore for paths that bypass the orderly shutdown
            // (Environment.Exit from fatal handlers, session end). A hard kill
            // cannot run this; the next start repairs it from the durable
            // marker, and Explorer itself shows the icons again on restart.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _mappedOrganizeService?.RestoreIconsForExit("process-exit");
            // A crash on a background thread ends the process: best effort.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.IsTerminating)
                {
                    _mappedOrganizeService?.RestoreIconsForExit("crash");
                }
            };
            ForkSessionEndSignal.Cancelled += () => _mappedOrganizeService?.ResumeAfterCancelledSessionEnd();
        }
        catch (Exception ex)
        {
            Log($"[Startup] Optional step 'mapped-organize' failed: {ex}");
        }
    }

    /// <summary>Synchronous restore of every DeskBox-hidden desktop item; safe to call repeatedly.</summary>
    private void RestoreMappedOrganizeBeforeExit(string reason, bool sessionEnd = false) =>
        _mappedOrganizeService?.RestoreIconsForExit(reason, sessionEnd);

    private void ShutdownMappedOrganize()
    {
        _mappedOrganizeService?.Dispose();
        _mappedOrganizeService = null;
        MappedOrganizeService.Current = null;
    }
}
