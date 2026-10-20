using System.Globalization;

namespace KeepingYouAwake.Core;

/// <summary>
/// User-facing text in English and Japanese, chosen by <see cref="CultureInfo.CurrentUICulture"/>.
/// Add a language by adding a column to <see cref="Table"/>.
/// </summary>
public static class Strings
{
    private static bool? japaneseOverride;

    /// <summary>Forces a language (tests). Null follows the UI culture.</summary>
    public static void OverrideLanguage(string? twoLetterCode) =>
        japaneseOverride = twoLetterCode is null ? null : twoLetterCode == "ja";

    public static bool IsJapanese =>
        japaneseOverride ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

    private static readonly Dictionary<string, (string En, string Ja)> Table = new()
    {
        // Generic
        ["OK"] = ("OK", "OK"),
        ["Cancel"] = ("Cancel", "キャンセル"),
        ["Add"] = ("Add…", "追加…"),
        ["Remove"] = ("Remove", "削除"),
        ["Edit"] = ("Edit…", "編集…"),
        ["Close"] = ("Close", "閉じる"),
        ["Refresh"] = ("Refresh", "更新"),
        ["AppName"] = ("KeepingYouAwake", "KeepingYouAwake"),

        // Tray menu
        ["ActivateForDuration"] = ("Activate for Duration", "指定時間だけ有効にする"),
        ["SetDefaultDuration"] = ("Default Duration", "デフォルトの時間"),
        ["Activate"] = ("Activate", "有効にする"),
        ["Deactivate"] = ("Deactivate", "無効にする"),
        ["SettingsEllipsis"] = ("Settings…", "設定…"),
        ["ShowActivityLogEllipsis"] = ("Show Activity Log…", "アクティビティログを表示…"),
        ["Quit"] = ("Quit", "終了"),
        ["IsDefaultSuffix"] = ("(Default)", "(デフォルト)"),
        ["Indefinitely"] = ("Indefinitely", "無期限"),
        ["UntilEndOfDay"] = ("Until end of day", "今日の終わりまで"),
        ["RemainingFormat"] = ("Remaining: {0}", "残り時間: {0}"),
        ["TooltipActive"] = ("KeepingYouAwake — preventing sleep", "KeepingYouAwake — スリープを防止中"),
        ["TooltipActiveRemaining"] = ("KeepingYouAwake — {0} left", "KeepingYouAwake — 残り {0}"),
        ["TooltipInactive"] = ("KeepingYouAwake — inactive", "KeepingYouAwake — 停止中"),
        ["StartedByFormat"] = ("Started by: {0}", "開始元: {0}"),

        // Notifications
        ["PreventingSleepTitle"] = ("Preventing Sleep", "スリープを防止しています"),
        ["PreventingSleepBody"] = ("You can still put your PC to sleep manually.", "手動でスリープさせることはできます。"),
        ["PreventingSleepForTitle"] = ("Preventing Sleep for {0}", "{0}の間スリープを防止します"),
        ["PreventingSleepForBody"] = ("Afterwards your PC will go to sleep again when unused.", "その後は、使っていないと通常どおりスリープします。"),
        ["AllowingSleepTitle"] = ("Allowing Sleep", "スリープを許可しました"),
        ["AllowingSleepBody"] = ("Your PC will go to sleep when unused.", "使っていないと PC はスリープします。"),

        // Settings window
        ["SettingsTitle"] = ("KeepingYouAwake Settings", "KeepingYouAwake の設定"),
        ["TabGeneral"] = ("General", "一般"),
        ["TabDurations"] = ("Durations", "時間"),
        ["TabBattery"] = ("Battery", "バッテリー"),
        ["TabWatchedItems"] = ("Watched Items", "監視項目"),
        ["TabAdvanced"] = ("Advanced", "詳細"),
        ["TabUpdates"] = ("Updates", "アップデート"),
        ["TabAbout"] = ("About", "情報"),

        ["StartAtLogin"] = ("Start at Login", "ログイン時に起動"),
        ["StartAtLoginHint"] = ("Automatically opens the app when you sign in to Windows.", "Windows にサインインしたときに自動で起動します。"),
        ["ActivateOnLaunch"] = ("Activate on Launch", "起動時に有効にする"),
        ["ActivateOnLaunchHint"] = ("Immediately starts preventing sleep when launched.", "起動するとすぐにスリープ防止を開始します。"),
        ["ShowNotifications"] = ("Show notifications when a session starts or ends", "セッションの開始・終了時に通知を表示"),
        ["LeftClickHint"] = ("Click the tray icon to turn sleep prevention on or off. Right-click it for more options.",
                             "トレイアイコンをクリックするとスリープ防止のオン/オフを切り替えます。右クリックでその他のメニューを表示します。"),

        ["DurationsHint"] = ("The default activation duration is used when you click the tray icon.",
                             "トレイアイコンをクリックしたときはデフォルトの時間が使われます。"),
        ["SetDefault"] = ("Set Default", "デフォルトに設定"),
        ["ResetEllipsis"] = ("Reset…", "リセット…"),
        ["AddDuration"] = ("Add Duration", "時間を追加"),
        ["Hours"] = ("Hours", "時間"),
        ["Minutes"] = ("Minutes", "分"),
        ["Seconds"] = ("Seconds", "秒"),
        ["DurationAlreadyAdded"] = ("This duration has already been added.", "この時間はすでに追加されています。"),
        ["DurationInvalid"] = ("The entered duration is invalid. Please try again.", "入力された時間が正しくありません。もう一度入力してください。"),
        ["ResetDurationsTitle"] = ("Reset Activation Durations", "時間をリセット"),
        ["ResetDurationsMessage"] = ("Do you really want to reset the activation durations to the default values?",
                                     "時間の一覧を初期値に戻しますか？"),

        ["BatteryThreshold"] = ("Deactivate when battery capacity is below", "バッテリー残量が次の値を下回ったら無効にする"),
        ["BatteryThresholdHint"] = ("This value is ignored when you manually start a session below this battery capacity.",
                                    "この残量を下回った状態で手動で開始したセッションには適用されません。"),
        ["BatterySaver"] = ("Deactivate when Battery Saver (Energy Saver) is on", "バッテリー節約機能（省電力モード）がオンになったら無効にする"),
        ["BatterySaverHint"] = ("Battery Saver can be turned on in Settings › System › Power & battery.",
                                "バッテリー節約機能は［設定］›［システム］›［電源とバッテリー］でオンにできます。"),
        ["NoBattery"] = ("No battery was detected on this PC.", "この PC ではバッテリーが検出されませんでした。"),
        ["BatteryStatusFormat"] = ("Current battery level: {0:0}%", "現在のバッテリー残量: {0:0}%"),

        ["WatchedWiFi"] = ("Watched Wi-Fi Networks", "監視する Wi-Fi ネットワーク"),
        ["WatchedWiFiHint"] = ("Stay active while connected to one of these Wi-Fi networks (SSIDs).",
                               "これらの Wi-Fi ネットワーク（SSID）に接続している間は有効のままにします。"),
        ["AddCurrentNetworkFormat"] = ("Add Current Network: {0}", "現在のネットワークを追加: {0}"),
        ["EnterNetworkName"] = ("Enter Network Name…", "ネットワーク名を入力…"),
        ["NotConnectedToWiFi"] = ("Not connected to a Wi-Fi network", "Wi-Fi ネットワークに接続していません"),
        ["NetworkNameUnreadable"] = ("The current network name can't be read. Allow location access for desktop apps in Settings › Privacy & security › Location.",
                                     "現在のネットワーク名を読み取れません。［設定］›［プライバシーとセキュリティ］›［位置情報］でデスクトップアプリの位置情報へのアクセスを許可してください。"),
        ["OpenLocationSettings"] = ("Open Location Settings…", "位置情報の設定を開く…"),
        ["NetworkName"] = ("Network name (SSID):", "ネットワーク名 (SSID):"),

        ["WatchedApplications"] = ("Watched Applications", "監視するアプリ"),
        ["WatchedApplicationsHint"] = ("Stay active while one of these applications is running.",
                                       "これらのアプリのいずれかが実行中の間は有効のままにします。"),
        ["ChooseApplication"] = ("Choose Application", "アプリを選択"),
        ["ChooseApplicationFile"] = ("Choose Program File…", "プログラムファイルを選択…"),
        ["ChooseRunningApplication"] = ("Choose from Running Apps…", "実行中のアプリから選択…"),
        ["ApplicationFilter"] = ("Programs (*.exe)|*.exe", "プログラム (*.exe)|*.exe"),

        ["WatchedDownloadFolders"] = ("Watched Download Folders", "監視するダウンロードフォルダー"),
        ["WatchedDownloadFoldersHint"] = ("Stay active while a download is in progress in one of these folders. With no folders listed, your Downloads folder is watched.",
                                          "これらのフォルダーでダウンロードが進行中の間は有効のままにします。一覧が空のときは「ダウンロード」フォルダーを監視します。"),
        ["ChooseFolder"] = ("Choose Folder", "フォルダーを選択"),

        ["ActiveHours"] = ("Active Hours", "アクティブ時間"),
        ["ActiveHoursHint"] = ("Stay active during these weekday and time windows. If the end time is earlier than the start time, the window wraps past midnight into the next day.",
                               "これらの曜日と時間帯は有効のままにします。終了時刻が開始時刻より前のときは、深夜 0 時をまたいで翌日まで続きます。"),
        ["From"] = ("From", "開始"),
        ["To"] = ("To", "終了"),
        ["Weekdays"] = ("Days", "曜日"),
        ["ActiveHoursWindow"] = ("Active Hours Window", "アクティブ時間帯"),
        ["SelectAtLeastOneDay"] = ("Select at least one day.", "曜日を 1 つ以上選んでください。"),
        ["WatchedItemToggleHint"] = ("Uncheck to pause an item without removing it.", "チェックを外すと、削除せずに一時停止できます。"),
        ["TriggersNeedEnablingHint"] = ("Downloads and Active Hours also need their switch in the Advanced tab.",
                                        "ダウンロードとアクティブ時間は、［詳細］タブのスイッチもオンにする必要があります。"),

        ["AdvancedHint"] = ("Only change these advanced settings if you know what you are doing.", "詳細設定は内容を理解したうえで変更してください。"),
        ["ResetToDefault"] = ("Reset to Default", "初期設定に戻す"),
        ["QuitOnTimerExpiration"] = ("Quit when activation duration is over", "時間が終了したらアプリを終了する"),
        ["AllowDisplaySleep"] = ("Allow the display to turn off", "ディスプレイの電源オフを許可する"),
        ["PreventDiskSleep"] = ("Prevent the system disk from sleeping", "システムディスクのスリープを防止する"),
        ["ActivateOnExternalDisplay"] = ("Activate when an external display is connected", "外部ディスプレイを接続したら有効にする"),
        ["DeactivateOnUserSwitch"] = ("Deactivate when switched to another user account", "別のユーザーに切り替えたら無効にする"),
        ["DeactivateOnFullCharge"] = ("Deactivate when battery is fully charged", "バッテリーがフル充電になったら無効にする"),
        ["DriveAlive"] = ("Keep other drives (external and secondary) spinning while active", "有効な間はシステム以外のドライブ（外付け・増設）の回転を維持する"),
        ["ActivateOnACPower"] = ("Activate while on AC power", "AC 電源に接続している間は有効にする"),
        ["HideCountdown"] = ("Hide the remaining time in the tray icon tooltip", "トレイアイコンのツールチップに残り時間を表示しない"),
        ["ActivateDuringSchedule"] = ("Activate during Active Hours", "アクティブ時間の間は有効にする"),
        ["ActivateWhileDownloading"] = ("Activate while a download is in progress", "ダウンロード中は有効にする"),
        ["MouseJiggler"] = ("Periodically nudge the mouse pointer while active", "有効な間はマウスポインターを定期的に動かす"),
        ["ActivateOnExternalAudio"] = ("Activate while audio plays through an external device (Bluetooth, USB, HDMI)", "外部デバイス（Bluetooth・USB・HDMI）から音声を出力している間は有効にする"),
        ["ActivateOnCpuLoad"] = ("Activate while CPU usage stays above", "CPU 使用率が次の値を上回っている間は有効にする"),

        ["AutomaticallyCheckForUpdates"] = ("Automatically check for updates", "アップデートを自動で確認する"),
        ["PreReleaseUpdates"] = ("Include pre-release versions", "プレリリース版も対象にする"),
        ["CheckNow"] = ("Check Now", "今すぐ確認"),
        ["LastCheckedFormat"] = ("Last checked: {0}", "最終確認: {0}"),
        ["NeverChecked"] = ("Never checked", "未確認"),
        ["UpdateAvailableTitle"] = ("Update Available", "アップデートがあります"),
        ["UpdateAvailableFormat"] = ("KeepingYouAwake {0} is available (you have {1}). Open the download page?",
                                     "KeepingYouAwake {0} が公開されています（現在 {1}）。ダウンロードページを開きますか？"),
        ["UpToDateFormat"] = ("You're up to date. KeepingYouAwake {0} is the newest version.", "最新です。KeepingYouAwake {0} が最新バージョンです。"),
        ["UpdateCheckFailedFormat"] = ("Couldn't check for updates: {0}", "アップデートを確認できませんでした: {0}"),

        ["VersionFormat"] = ("Version {0}", "バージョン {0}"),
        ["AboutDescription"] = ("Prevents your PC from going to sleep — for a set time, or while a trigger such as a running app, a Wi-Fi network or a download stays active.",
                                "決めた時間のあいだ、またはアプリの実行中・Wi-Fi 接続中・ダウンロード中などのトリガーが続く間、PC をスリープさせません。"),
        ["AboutCredits"] = ("A Windows port of KeepingYouAwake (Amphetamine) by ryouka0731, based on KeepingYouAwake by Marcel Dierkes. MIT License.",
                            "ryouka0731 による KeepingYouAwake (Amphetamine) の Windows 移植版です。Marcel Dierkes 氏の KeepingYouAwake をもとにしています。MIT ライセンス。"),
        ["OpenWebsite"] = ("Open Project Website", "プロジェクトのサイトを開く"),

        // Activity log
        ["ActivityLogTitle"] = ("Activity Log", "アクティビティログ"),
        ["ColumnStarted"] = ("Started", "開始"),
        ["ColumnEnded"] = ("Ended", "終了"),
        ["ColumnSource"] = ("Source", "開始元"),
        ["ColumnRequested"] = ("Requested", "指定時間"),
        ["ColumnReason"] = ("Reason", "終了理由"),
        ["StillActive"] = ("(active)", "（実行中）"),
        ["OpenLogFolder"] = ("Open Folder", "フォルダーを開く"),

        // Activation sources
        ["Source.user"] = ("You", "ユーザー"),
        ["Source.watched-app"] = ("Watched application", "監視アプリ"),
        ["Source.watched-ssid"] = ("Watched Wi-Fi network", "監視 Wi-Fi"),
        ["Source.ac-power"] = ("AC power", "AC 電源"),
        ["Source.external-display"] = ("External display", "外部ディスプレイ"),
        ["Source.schedule"] = ("Active Hours", "アクティブ時間"),
        ["Source.download"] = ("Download", "ダウンロード"),
        ["Source.audio-output"] = ("External audio", "外部音声出力"),
        ["Source.cpu-load"] = ("CPU load", "CPU 負荷"),

        // Ended reasons
        ["Reason.expired"] = ("Time is up", "時間切れ"),
        ["Reason.user-cancelled"] = ("Stopped", "停止"),
        ["Reason.trigger-cancelled"] = ("Trigger ended", "トリガー終了"),
        ["Reason.app-terminated"] = ("App quit", "アプリ終了"),

        // Duration units
        ["Unit.hour"] = ("{0} hour", "{0}時間"),
        ["Unit.hours"] = ("{0} hours", "{0}時間"),
        ["Unit.minute"] = ("{0} minute", "{0}分"),
        ["Unit.minutes"] = ("{0} minutes", "{0}分"),
        ["Unit.second"] = ("{0} second", "{0}秒"),
        ["Unit.seconds"] = ("{0} seconds", "{0}秒"),
        ["Unit.separator"] = (", ", ""),
    };

    public static string Get(string key) =>
        Table.TryGetValue(key, out var value) ? (IsJapanese ? value.Ja : value.En) : key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static IEnumerable<string> Keys => Table.Keys;

    public static string SourceName(string logSource) =>
        Table.ContainsKey("Source." + logSource) ? Get("Source." + logSource) : logSource;

    public static string ReasonName(string? reason) =>
        reason is null ? string.Empty : Table.ContainsKey("Reason." + reason) ? Get("Reason." + reason) : reason;

    /// <summary>"1 hour, 30 minutes" / "1時間30分"; "Indefinitely" for zero.</summary>
    public static string DurationTitle(ActivationDuration duration) =>
        duration.IsIndefinite ? Get("Indefinitely") : DurationText(TimeSpan.FromSeconds(duration.Seconds));

    public static string DurationText(TimeSpan span)
    {
        var parts = new List<string>();
        var hours = (long)span.TotalHours;
        if (hours > 0) { parts.Add(Format(hours == 1 ? "Unit.hour" : "Unit.hours", hours)); }
        if (span.Minutes > 0) { parts.Add(Format(span.Minutes == 1 ? "Unit.minute" : "Unit.minutes", span.Minutes)); }
        if (span.Seconds > 0) { parts.Add(Format(span.Seconds == 1 ? "Unit.second" : "Unit.seconds", span.Seconds)); }
        if (parts.Count == 0) { parts.Add(Format("Unit.seconds", 0)); }
        return string.Join(Get("Unit.separator"), parts);
    }

    /// <summary>Countdown text, e.g. <c>1:05:09</c> or <c>4:59</c>.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) { remaining = TimeSpan.Zero; }
        // Round up so "0:00" only shows when the session has ended.
        var total = (long)Math.Ceiling(remaining.TotalSeconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>Short weekday names, Sunday first.</summary>
    public static string WeekdayName(DayOfWeek day) =>
        IsJapanese
            ? "日月火水木金土"[(int)day].ToString()
            : CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)day];
}
