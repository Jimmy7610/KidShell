namespace KidShell.App.Localization;

/// <summary>
/// Every user-facing string in KidShell lives here, keyed.
///
/// MVP 0.1 ships Swedish only. The indirection is the point: XAML and view
/// models ask for a key, never for a literal, so adding a second language is
/// a matter of adding another table (or swapping this lookup for a
/// ResourceLoader) rather than hunting text through the UI.
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, string> Swedish = new(StringComparer.Ordinal)
    {
        // ---------- Brand / shell ----------
        ["App.Title"] = "KidShell",
        ["Child.Wordmark"] = "Barnläge",
        ["Child.Tagline"] = "Upptäck. Lek. Lär. Var dig själv.",
        ["Child.FooterTagline"] = "En tryggare digital vardag",
        ["Child.Settings"] = "Inställningar",
        ["Child.ParentAccess"] = "För vuxna",
        ["Child.ParentAccessHint"] = "Håll in i tre sekunder",
        ["Child.ParentAccessAutomation"] = "För vuxna. Håll knappen intryckt i tre sekunder för att öppna föräldraläget.",
        ["Child.Greeting"] = "Hej {0}!",
        // Screen time, as a child reads it
        ["Child.TimeUpTitle"] = "Tiden är slut för i dag",
        ["Child.TimeUpBody"] = "Bra jobbat! Gå och säg till en vuxen om du vill ha mer tid.",
        ["Child.OutsideHoursTitle"] = "Datorn vilar nu",
        ["Child.OutsideHoursBody"] = "Just nu är det inte datortid. Fråga en vuxen om du undrar.",
        ["Child.WarningMinutes"] = "{0} minuter kvar",
        ["Child.WarningOneMinute"] = "En minut kvar",
        ["Child.WarningOk"] = "Okej",

        ["Child.Encouragement"] = "Du är fantastisk!",
        ["Child.EmptyGridTitle"] = "Inga appar är påslagna",
        ["Child.EmptyGridBody"] = "En vuxen kan slå på appar i Föräldraläge.",
        ["Child.HoldProgressAutomation"] = "Håller in knappen för vuxna",

        // ---------- Launch feedback ----------
        ["Launch.NotConfiguredTitle"] = "Nästan klart",
        ["Launch.MissingTitle"] = "Hoppsan",
        ["Launch.FailedTitle"] = "Det gick inte just nu",
        ["Launch.Back"] = "Tillbaka",
        ["Launch.NotConfiguredHint"] = "En vuxen kan lägga till programmet i Föräldraläge.",

        // ---------- PIN ----------
        ["Pin.Title"] = "Föräldraläge",
        ["Pin.Prompt"] = "Ange din PIN-kod",
        ["Pin.Wrong"] = "Fel PIN-kod",
        ["Pin.Cancel"] = "Avbryt",
        ["Pin.Delete"] = "Radera siffra",
        ["Pin.Clear"] = "Rensa",
        ["Pin.DeveloperHint"] = "Utvecklingsläge: standard-PIN används tills en vuxen väljer en egen.",
        ["Pin.EntryAutomation"] = "PIN-kod, {0} av {1} siffror angivna",
        ["Pin.KeyAutomation"] = "Siffra {0}",
        ["Pin.ErrorEmpty"] = "Skriv en PIN-kod.",
        ["Pin.ErrorNotNumeric"] = "PIN-koden får bara innehålla siffror.",
        ["Pin.ErrorRepeated"] = "Välj en PIN-kod med olika siffror.",
        ["Pin.ErrorSequential"] = "Välj en PIN-kod som inte är en sifferföljd.",
        ["Pin.ErrorReserved"] = "Den PIN-koden är KidShells utvecklingskod och kan inte användas.",

        // ---------- Parent shell ----------
        ["Parent.Title"] = "Föräldraläge",
        ["Parent.Subtitle"] = "Hantera appar, säkerhet och skärmtid",
        ["Parent.ChildSummary"] = "{0}, {1} år",
        ["Parent.ChildMotto"] = "Tryggt, lärorikt, roligt!",
        ["Parent.BackToChild"] = "Tillbaka till Barnläge",
        ["Parent.ExitToWindows"] = "Avsluta till Windows",
        ["Parent.Save"] = "Spara ändringar",
        ["Parent.Saved"] = "Ändringarna är sparade.",
        ["Parent.SaveFailed"] = "Ändringarna kunde inte sparas. Se loggen för detaljer.",
        ["Parent.UnsavedBadge"] = "Osparade ändringar",
        ["Parent.Help"] = "Hjälp",

        // ---------- Parent navigation ----------
        ["Nav.Overview"] = "Översikt",
        ["Nav.Apps"] = "Appar",
        ["Nav.ScreenTime"] = "Skärmtid",
        ["Nav.Web"] = "Webb",
        ["Nav.Security"] = "Säkerhet",
        ["Nav.Profile"] = "Profil",
        ["Nav.Automation"] = "Sidor i Föräldraläge",

        // ---------- Overview ----------
        ["Overview.Title"] = "Översikt",
        ["Overview.Subtitle"] = "Så här är {0} dator inställd just nu.",
        ["Overview.AppsTitle"] = "Appar",
        ["Overview.AppsValue"] = "{0} tillåtna",
        ["Overview.AppsHint"] = "Av totalt {0} konfigurerade.",
        ["Overview.ScreenTimeTitle"] = "Skärmtid",
        ["Overview.ScreenTimeNoLimit"] = "Ingen gräns inställd",
        ["Overview.ScreenTimeConfigured"] = "{0} min vardag · {1} min helg",
        ["Overview.ScreenTimeHint"] = "Gäller i Barnläge. Windows är inte låst.",
        ["Overview.WebTitle"] = "Webbfilter",
        ["Overview.WebNotConfigured"] = "Inte konfigurerat",
        ["Overview.WebHint"] = "Valet sparas lokalt. Webbläsarregler skrivs först vid säker installation.",
        ["Overview.SecurityTitle"] = "Säkerhet",
        ["Overview.SecurityValue"] = "Utvecklingsläge",
        ["Overview.SecurityHint"] = "Windows är inte låst.",
        ["Overview.BannerTitle"] = "Windows är inte låst på den här datorn",
        ["Overview.BannerBody"] =
            "KidShell styr vad barnet ser och hur länge, men Windows självt är orört. Barnet kan fortfarande minimera KidShell och nå resten av datorn. Riktig låsning görs vid säker installation på en dator du valt för ändamålet.",

        // ---------- Apps page ----------
        ["Apps.Title"] = "Tillåtna appar",
        ["Apps.Subtitle"] = "Välj vilka appar {0} får använda på sin dator.",
        ["Apps.Add"] = "Lägg till app",
        ["Apps.Remove"] = "Ta bort",
        ["Apps.RemoveAutomation"] = "Ta bort {0}",
        ["Apps.ToggleAutomation"] = "Tillåt {0}",
        ["Apps.NotConfiguredTag"] = "Inget program valt",
        ["Apps.EnabledTag"] = "Tillåten",
        ["Apps.DisabledTag"] = "Avstängd",
        ["Apps.Count"] = "{0} av {1} appar är påslagna",

        // ---------- Installed application browser ----------
        ["Discover.Title"] = "Välj ett program",
        ["Discover.Subtitle"] = "Program som är installerade på den här datorn. Välj ett så fyller KidShell i resten.",
        ["Discover.Search"] = "Sök bland programmen",
        ["Discover.Refresh"] = "Sök igen",
        ["Discover.Scanning"] = "Letar efter installerade program...",
        ["Discover.Manual"] = "Lägg till manuellt",
        ["Discover.Add"] = "Lägg till",
        ["Discover.AlreadyAdded"] = "Tillagd",
        ["Discover.AutomationAdd"] = "Lägg till {0}",
        ["Discover.AutomationAdded"] = "{0} är redan tillagd",
        ["Discover.Count"] = "Visar {0} av {1} program.",
        ["Discover.NoMatches"] = "Inget program matchar \"{0}\".",
        ["Discover.NothingFound"] = "Inga program hittades. Du kan lägga till ett manuellt i stället.",
        ["Discover.ScanFailed"] = "Alla program kunde inte läsas. Listan kan vara ofullständig.",
        ["Discover.KindWin32"] = "Program",
        ["Discover.KindPackaged"] = "Microsoft Store",
        ["Discover.KindLauncher"] = "Startprogram",
        ["Discover.KindProtocol"] = "Systemlänk",
        ["Discover.KindUnknown"] = "Okänd typ",
        ["Discover.ReviewLauncher"] = "Startar andra program",
        ["Discover.ReviewBrowser"] = "Når hela webben",
        ["Discover.NotPermissionNote"] =
            "Att lägga till ett program visar det i Barnläge. Det är inte samma sak som att Windows tillåter det – det bestäms först vid säker installation.",

        // ---------- Add app dialog ----------
        ["AddApp.Title"] = "Lägg till app",
        ["AddApp.NameLabel"] = "Namn som barnet ser",
        ["AddApp.NamePlaceholder"] = "Till exempel Rita",
        ["AddApp.ProgramLabel"] = "Programmets namn",
        ["AddApp.ProgramPlaceholder"] = "Till exempel Paint",
        ["AddApp.PathLabel"] = "Sökväg till program",
        ["AddApp.PathPlaceholder"] = "C:\\Program Files\\...\\program.exe",
        ["AddApp.Browse"] = "Välj program...",
        ["AddApp.ArgumentsLabel"] = "Argument (valfritt)",
        ["AddApp.CategoryLabel"] = "Kategori",
        ["AddApp.ColorLabel"] = "Färg",
        ["AddApp.IconLabel"] = "Ikon",
        ["AddApp.Save"] = "Lägg till",
        ["AddApp.Cancel"] = "Avbryt",
        ["AddApp.NameRequired"] = "Ge appen ett namn först.",
        ["AddApp.PathMissing"] = "Programmet hittades inte på den här datorn.",
        ["AddApp.PickerFailed"] = "Filväljaren kunde inte öppnas.",

        // ---------- Screen time ----------
        ["ScreenTime.Title"] = "Skärmtid",
        ["ScreenTime.Subtitle"] = "Bestäm hur länge {0} får använda datorn.",
        ["ScreenTime.Enable"] = "Aktivera skärmtid",
        ["ScreenTime.Weekdays"] = "Vardagar (mån–fre)",
        ["ScreenTime.Weekends"] = "Helger (lör–sön)",
        ["ScreenTime.Minutes"] = "{0} minuter",
        ["ScreenTime.HoursAndMinutes"] = "{0} h {1} min",
        ["ScreenTime.Hours"] = "{0} timmar",
        ["ScreenTime.OneHour"] = "1 timme",
        ["ScreenTime.NoticeTitle"] = "Gäller i Barnläge, inte i hela Windows",
        ["ScreenTime.NoticeBody"] =
            "KidShell håller koll på tiden och slutar starta program när den tar slut. Så länge Windows inte är låst kan barnet fortfarande lämna Barnläge – då gäller inte tiden.",

        // Daily window
        ["ScreenTime.RestrictHours"] = "Begränsa när datorn får användas",
        ["ScreenTime.RestrictHoursHint"] = "Ett barn med en timme kvar klockan 23 ska ändå sova.",
        ["ScreenTime.From"] = "Från",
        ["ScreenTime.Until"] = "Till",
        ["ScreenTime.HoursSummary"] = "Datorn får användas mellan {0} och {1}.",
        ["ScreenTime.HoursOff"] = "Datorn får användas när som helst på dygnet.",

        // Today
        ["ScreenTime.TodayTitle"] = "I dag",
        ["ScreenTime.Used"] = "Använt",
        ["ScreenTime.Remaining"] = "Kvar",
        ["ScreenTime.StatusOff"] = "Skärmtid är avstängd. Ingen tid räknas.",
        ["ScreenTime.UnsavedNotice"] =
            "Du har ändringar som inte är sparade. Siffrorna här visar det som gäller just nu – spara för att de nya tiderna ska börja räknas.",
        ["ScreenTime.StatusUnknown"] = "Tiden har inte räknats i dag än.",
        ["ScreenTime.StatusExpired"] = "Dagens tid är slut.",
        ["ScreenTime.StatusOutsideHours"] = "Datorn får inte användas just nu.",
        ["ScreenTime.StatusRemaining"] = "{0} kvar i dag.",
        ["ScreenTime.BonusGranted"] = "Du har gett {0} extra i dag.",
        ["ScreenTime.ClockWarning"] =
            "Datorns klocka har ställts tillbaka. Tiden räknas från en klocka som inte går att ändra, så ingen tid har försvunnit.",

        // Extensions
        ["ScreenTime.ExtendTitle"] = "Ge mer tid i dag",
        ["ScreenTime.ExtendHint"] = "Gäller bara i dag och börjar gälla direkt.",
        ["ScreenTime.Extend15"] = "+15 min",
        ["ScreenTime.Extend30"] = "+30 min",
        ["ScreenTime.Extend60"] = "+1 timme",
        ["ScreenTime.ExtendRestOfDay"] = "Resten av dagen",
        ["ScreenTime.ResetToday"] = "Nollställ dagen",
        ["ScreenTime.GrantedExtension"] = "{0} extra tillagt.",
        ["ScreenTime.GrantedRestOfDay"] = "Resten av dagen är fri.",
        ["ScreenTime.ResetDone"] = "Dagens räknare är nollställd.",

        // ---------- About ----------
        ["About.Title"] = "Om KidShell",
        ["About.Subtitle"] = "Version, byggläge och vad den här datorn klarar.",
        ["About.Version"] = "Version",
        ["About.Build"] = "Byggläge",
        ["About.Commit"] = "Ändring {0}",
        ["About.BuildRelease"] = "Release",
        ["About.BuildDevelopment"] = "UTVECKLING",
        ["About.DevelopmentWarning"] =
            "Det här är ett utvecklingsbygge. Det accepterar en PIN-kod som står i dokumentationen och ska inte användas av ett barn på riktigt.",
        ["About.Windows"] = "Windows",
        ["About.Build.Number"] = "Build {0} · {1}",
        ["About.StandardMode"] = "Standardläge",
        ["About.StandardHint"] = "Fungerar på alla Windows-utgåvor.",
        ["About.SecureMode"] = "Säkert läge",
        ["About.SecureHint"] = "Den här utgåvan stöder Windows begränsade läge.",
        ["About.SecureUnsupportedHint"] = "{0} har inte Windows begränsade läge. Det kräver Pro, Enterprise, Education eller IoT Enterprise.",
        ["About.AppControl"] = "Appkontroll i Windows",
        ["About.AppControlHint"] =
            "Att Windows kan spärra program är inte samma sak som att det går att installera reglerna här. KidShell skiljer på de två.",
        ["About.SecurityStatus"] = "Säkerhetsläge",
        ["About.SecurityNotApplied"] = "Inget är tillämpat",
        ["About.SecurityNotAppliedHint"] = "KidShell har inte ändrat någon Windows-inställning på den här datorn.",
        ["About.Updates"] = "Uppdateringar",
        ["About.UpdatesOn"] = "På",
        ["About.UpdatesOff"] = "Avstängda",
        ["About.UpdatesHint"] =
            "Automatiska uppdateringar är avstängda tills paketet är signerat. En uppdaterare som installerar osignerade paket är en säkerhetsrisk.",
        ["About.Licence"] = "Licens",
        ["About.LicenceValue"] = "MIT-licens. Öppen källkod.",
        ["About.Available"] = "Tillgängligt",
        ["About.PartlyAvailable"] = "Delvis",
        ["About.NotSupported"] = "Stöds inte",

        // ---------- Web ----------
        ["Web.Title"] = "Webbfilter",
        ["Web.Subtitle"] = "Välj hur mycket av internet {0} kommer åt.",
        ["Web.ModeNone"] = "Ingen webbläsare",
        ["Web.ModeNoneHint"] = "Barnet kommer inte åt internet från Barnläge.",
        ["Web.ModeAllowlist"] = "Endast godkända sidor",
        ["Web.ModeAllowlistHint"] = "Barnet kan bara besöka sidor du har godkänt.",
        ["Web.ModeOpen"] = "Friare webb",
        ["Web.ModeOpenHint"] = "Barnet kan surfa fritt. Rekommenderas inte för sexåringar.",
        ["Web.AllowlistTitle"] = "Godkända sidor",
        ["Web.AddDomainPlaceholder"] = "till exempel svt.se",
        ["Web.AddDomain"] = "Lägg till",
        ["Web.RemoveDomainAutomation"] = "Ta bort {0}",
        ["Web.PreviewTitle"] = "Det här skrivs vid säker installation",
        ["Web.PreviewSubtitle"] =
            "Exakt de här värdena sätts i Microsoft Edge. Ingenting skrivs härifrån – det sker först när du genomför säker installation på en dator du valt.",

        ["Web.EmptyAllowlist"] = "Inga godkända sidor ännu.",
        ["Web.NoticeTitle"] = "Valet sparas lokalt",
        ["Web.NoticeBody"] =
            "MVP 0.1 ändrar inga webbläsarinställningar och sätter inga Edge-policyer. Listan används av en senare milstolpe.",

        // ---------- Security ----------
        ["Security.Title"] = "Säkerhet",
        ["Security.Subtitle"] = "Ärlig status för den här datorn.",
        ["Security.WindowsLock"] = "Windows-låsning",
        ["Security.ChildAccount"] = "Barnkonto",
        ["Security.Allowlist"] = "Allowlist",
        ["Security.Watchdog"] = "Watchdog",
        ["Security.ParentPin"] = "Föräldra-PIN",
        ["Security.NotEnabled"] = "Inte aktiverad",
        ["Security.NotConfigured"] = "Inte konfigurerat",
        ["Security.NotInstalled"] = "Inte installerad",
        ["Security.Active"] = "Aktiv",
        ["Security.DevelopmentPinActive"] = "Aktiv (standard-PIN)",
        ["Security.BannerTitle"] = "Det här är en prototyp",
        ["Security.BannerBody"] =
            "KidShell har inte ändrat något i Windows: inga konton, ingen Assigned Access, ingen AppLocker, inga grupprinciper, inget register. Ett barn kan stänga KidShell som vilket program som helst.",
        ["Security.ChangePin"] = "Ändra PIN-kod",
        ["Security.ConfigPathTitle"] = "Inställningsfil",
        ["Security.LogPathTitle"] = "Logg",
        ["Security.LocalOnly"] = "Allt sparas lokalt. KidShell skickar ingenting någonstans.",

        // ---------- Profile ----------
        ["Profile.Title"] = "Profil",
        ["Profile.Subtitle"] = "Namn, ålder och utseende i Barnläge.",
        ["Profile.NameLabel"] = "Namn",
        ["Profile.AgeLabel"] = "Ålder",
        ["Profile.AvatarLabel"] = "Figur",
        ["Profile.ThemeLabel"] = "Tema",
        ["Profile.AvatarAutomation"] = "Välj figuren {0}",
        ["Profile.PreviewTitle"] = "Så här ser det ut",

        // ---------- First-run onboarding ----------
        ["Setup.StepOf"] = "Steg {0} av {1}",

        ["Setup.WelcomeTitle"] = "Välkommen till Barnläge",
        ["Setup.WelcomeBody"] = "Nu gör vi datorn personlig och trygg.",
        ["Setup.WelcomeStart"] = "Kom igång",
        ["Setup.WelcomeHint"] = "Det tar en minut. Du kan ändra allt senare i Föräldraläge.",

        ["Setup.NameTitle"] = "Vad heter barnet som ska använda datorn?",
        ["Setup.NameBody"] = "Namnet används för att hälsa på barnet i Barnläge.",
        ["Setup.NamePlaceholder"] = "Skriv barnets namn",
        ["Setup.NameLabel"] = "Barnets namn",
        ["Setup.NameEmpty"] = "Skriv barnets namn för att fortsätta.",
        ["Setup.NameTooLong"] = "Namnet är för långt. Välj ett kortare namn.",

        ["Setup.AvatarTitle"] = "Välj en avatar till {0}",
        ["Setup.AvatarBody"] = "Den här figuren möter {0} varje gång datorn startar.",
        ["Setup.AvatarRequired"] = "Välj en figur för att fortsätta.",
        ["Setup.AvatarAutomation"] = "Figur {0}",
        ["Setup.AvatarSelectedAutomation"] = "Figur {0}, vald",

        ["Setup.AgeTitle"] = "Hur gammal är {0}?",
        ["Setup.AgeBody"] = "Åldern sparas i profilen. Den ändrar inga säkerhetsinställningar.",
        ["Setup.AgeRequired"] = "Välj en ålder för att fortsätta.",
        ["Setup.AgeOpenEnded"] = "10+",
        ["Setup.AgeYears"] = "{0} år",
        ["Setup.AgeOpenEndedAutomation"] = "10 år eller äldre",

        ["Setup.ThemeTitle"] = "Välj hur Barnläge ska se ut",
        ["Setup.ThemeBody"] = "Temat bestämmer bakgrunden som {0} ser.",
        ["Setup.ThemeRequired"] = "Välj ett tema för att fortsätta.",

        ["Setup.DoneTitle"] = "Allt är klart för {0}!",
        ["Setup.DoneBody"] = "Nu är Barnläge redo att användas.",
        ["Setup.DoneStart"] = "Starta Barnläge",
        ["Setup.DoneSummary"] = "{0}, {1} år",
        ["Setup.SaveFailed"] = "Inställningarna kunde inte sparas. Försök igen.",

        ["Setup.Continue"] = "Fortsätt",
        ["Setup.Back"] = "Tillbaka",

        // ---------- Theme names ----------
        ["Theme.forest"] = "Skogen",
        ["Theme.space"] = "Rymden",
        ["Theme.ocean"] = "Havet",
        ["Theme.dino"] = "Dinosaurier",
        ["Theme.bright"] = "Färgglatt",
        ["Theme.forest.Hint"] = "Gröna kullar och en lugn sjö",
        ["Theme.space.Hint"] = "Stjärnhimmel och måne",
        ["Theme.ocean.Hint"] = "Öppet hav så långt man ser",
        ["Theme.dino.Hint"] = "Varmt urtidsljus och en vulkan",
        ["Theme.bright.Hint"] = "Starka färger överallt",

        // ---------- Re-run onboarding ----------
        ["Profile.RerunOnboarding"] = "Kör introduktionen igen",
        ["Profile.RerunTitle"] = "Kör introduktionen igen?",
        ["Profile.RerunBody"] =
            "Namn, ålder och figur nollställs och introduktionen startar om. Appar, skärmtid, webbinställningar och PIN-koden påverkas inte.",
        ["Profile.RerunPrimary"] = "Starta om introduktionen",
        ["Profile.RerunFailed"] = "Introduktionen kunde inte startas om. Se loggen för detaljer.",
        ["Profile.RerunUnsaved"] =
            "Spara eller kasta dina ändringar innan du kör introduktionen igen.",

        // ---------- Security readiness (MVP 0.1.5) ----------
        ["Security.StatusTitle"] = "Säkerhetsstatus",
        ["Security.LockNotEnabled"] = "Windows-låsning är inte aktiverad",
        ["Security.ScanFailedTitle"] = "Kontrollen kunde inte köras",

        ["Security.StateDevelopment"] = "Utvecklingsläge",
        ["Security.StateDevelopmentBody"] =
            "KidShell har inte ändrat något i Windows. Barnet kan fortfarande minimera KidShell och nå resten av datorn.",
        ["Security.StateNotReady"] = "Inte redo",
        ["Security.StateNotReadyBody"] = "Något behöver åtgärdas innan säkerhetsinstallation kan köras.",
        ["Security.StateReadyWithWarnings"] = "Redo med anmärkningar",
        ["Security.StateReadyWithWarningsBody"] = "Installationen kan köras, men läs anmärkningarna först.",
        ["Security.StateReady"] = "Redo att konfigureras",
        ["Security.StateReadyBody"] = "Allt som behövs för det rekommenderade läget finns på plats.",

        // Windows information card
        ["Security.WindowsInfoTitle"] = "Windows-information",
        ["Security.EditionLabel"] = "Windows-version",
        ["Security.BuildLabel"] = "Build",
        ["Security.AccountTypeLabel"] = "Kontotyp",
        ["Security.AccountAdministrator"] = "Administratör",
        ["Security.AccountStandardUser"] = "Standardanvändare",
        ["Security.UacLabel"] = "UAC",
        ["Security.UacActive"] = "Aktivt",
        ["Security.UacInactive"] = "Inte aktivt",
        ["Security.UacUnknown"] = "Kunde inte läsas",

        // Capability cards
        ["Security.CapabilitiesTitle"] = "Funktioner på den här datorn",
        ["Security.CapAvailable"] = "Tillgängligt",
        ["Security.CapUnavailable"] = "Inte tillgängligt",
        ["Security.CapUnknown"] = "Okänt",

        ["Security.CapAssignedAccess"] = "Assigned Access",
        ["Security.CapAssignedAccessAvailable"] = "Kan användas i en senare säkerhetsinstallation.",
        ["Security.CapAssignedAccessUnavailable"] = "Kräver Windows 11 Pro eller senare.",

        ["Security.CapAppControl"] = "Appkontroll i Windows (AppLocker)",
        ["Security.CapAppControlEnforceable"] =
            "Den här Windows-versionen kan spärra andra program. Stöds på alla utgåvor.",
        ["Security.CapAppControlNoChannel"] =
            "Windows kan spärra program här, men saknar inbyggt sätt att installera reglerna på Home.",
        ["Security.CapAppControlUnavailable"] =
            "Den här Windows-versionen stöder inte AppLocker.",
        ["Security.CapAppControlPartial"] = "Delvis",

        // Advanced breakdown of the AppLocker surface
        ["Security.DiagAppLockerEnforcement"] = "AppLocker-spärr",
        ["Security.DiagAppLockerService"] = "Application Identity (AppIDSvc)",
        ["Security.DiagAppLockerPowerShell"] = "AppLocker PowerShell-modul",
        ["Security.DiagAppLockerLocalPolicy"] = "Lokal AppLocker-policy läsbar",
        ["Security.DiagAppLockerStore"] = "Lokal policylagring",
        ["Security.DiagAppLockerUi"] = "Principhanterare (secpol/gpedit)",
        ["Security.DiagAppLockerCsp"] = "AppLocker CSP (MDM)",
        ["Security.DiagAppLockerChannel"] = "Installationsväg för regler",
        ["Security.DiagChannelNone"] = "Ingen inbyggd",

        ["Security.CapKidShellAllowlist"] = "KidShells applista",
        ["Security.CapKidShellAllowlistHint"] =
            "Styr vilka appar barnet ser i KidShell. Ersätter inte Windows egen appkontroll.",

        ["Security.CapChildAccount"] = "Barnkonto",
        ["Security.CapChildAccountNone"] = "Inget separat barnkonto är konfigurerat ännu.",
        ["Security.CapChildAccountFound"] = "Möjligt konto hittat: {0}",
        ["Security.CapWatchdog"] = "Watchdog",
        ["Security.CapWatchdogNone"] = "Ingen övervakning är installerad.",
        ["Security.CapWebPolicy"] = "Webbpolicy",
        ["Security.CapWebPolicyNone"] = "Ingen webbläsarpolicy är aktiverad.",
        ["Security.NotConfiguredShort"] = "Inte konfigurerat",
        ["Security.NotInstalledShort"] = "Inte installerad",
        ["Security.NotEnabledShort"] = "Inte aktiverad",

        // Recommended mode card
        ["Security.RecommendedTitle"] = "Rekommenderat säkerhetsläge",
        ["Security.ModeStandard"] = "Standardläge",
        ["Security.ModeStandardBody"] =
            "Den här Windows-versionen saknar vissa funktioner som används av KidShell Secure Mode.",
        ["Security.ModeSecure"] = "Secure Mode tillgängligt",
        ["Security.ModeSecureBody"] =
            "Den här datorn stöder alla funktioner som KidShell Secure Mode använder.",
        ["Security.ModeDevelopment"] = "Endast utvecklingsläge",
        ["Security.ModeDevelopmentBody"] =
            "Den här datorn uppfyller inte kraven för vare sig Standardläge eller Secure Mode.",
        ["Security.ShowDifference"] = "Visa skillnaden",

        // Standard vs Secure comparison
        ["Security.CompareTitle"] = "Standardläge och Secure Mode",
        ["Security.CompareIntro"] =
            "Båda lägena gör datorn enklare och tryggare för barnet. Secure Mode kan dessutom låsa själva Windows-inloggningen.",
        ["Security.CompareFeature"] = "Funktion",
        ["Security.CompareStandard"] = "Standard",
        ["Security.CompareSecure"] = "Secure",
        ["Security.CompareYes"] = "Ja",
        ["Security.CompareNo"] = "Nej",
        ["Security.CompareLimited"] = "Begränsad",
        ["Security.CompareRowInterface"] = "KidShell-gränssnitt",
        ["Security.CompareRowChildAccount"] = "Separat barnkonto",
        ["Security.CompareRowAppLimits"] = "Appbegränsning",
        ["Security.CompareRowPin"] = "Föräldra-PIN",
        ["Security.CompareRowAssignedAccess"] = "Assigned Access",
        ["Security.CompareRowLockedEnvironment"] = "Förstärkt låst användarmiljö",
        ["Security.CompareFooter"] =
            "Inget av lägena gör datorn omöjlig att ta sig ur. De gör det svårare för ett barn att hamna fel av misstag.",
        ["Security.CompareClose"] = "Stäng",

        // Security plan
        ["Security.ShowPlan"] = "Visa säkerhetsplan",
        ["Security.PlanTitle"] = "Det här skulle KidShell göra",
        ["Security.PlanIntro"] =
            "Så här skulle säkerhetsinstallationen gå till på den här datorn. Ingenting av det körs nu — listan visas bara.",
        ["Security.PlanEmpty"] = "Den här datorn har inget läge att installera ännu.",
        ["Security.PlanNothingExecuted"] = "Inga av stegen har körts. Datorn är oförändrad.",
        ["Security.PlanRequiresAdmin"] = "Kräver administratör",
        ["Security.PlanCanRollback"] = "Går att ångra",
        ["Security.PlanCannotRollback"] = "Går inte att ångra automatiskt",
        ["Security.PlanRiskLow"] = "Låg ändringsrisk",
        ["Security.PlanRiskMedium"] = "Medelhög ändringsrisk",
        ["Security.PlanRiskHigh"] = "Hög ändringsrisk",
        ["Security.PlanRiskNote"] =
            "Ändringsrisk beskriver hur svårt ett steg är att ångra — inte hur säkert resultatet blir.",
        ["Security.PlanClose"] = "Stäng",

        // Checks, warnings, blockers
        ["Security.ChecksTitle"] = "Kontroller",
        ["Security.BlockersTitle"] = "Behöver åtgärdas",
        ["Security.WarningsTitle"] = "Att känna till",
        ["Security.CheckPassed"] = "Klart",
        ["Security.CheckWarning"] = "Anmärkning",
        ["Security.CheckFailed"] = "Behöver åtgärdas",
        ["Security.CheckNotApplicable"] = "Kunde inte kontrolleras",

        // Advanced diagnostics
        ["Security.Advanced"] = "Avancerat",
        ["Security.DiagEdition"] = "Edition",
        ["Security.DiagVersion"] = "Version",
        ["Security.DiagBuild"] = "Build",
        ["Security.DiagAssignedAccess"] = "Assigned Access-stöd",
        ["Security.DiagAppControl"] = "Appkontrollstöd",
        ["Security.DiagUac"] = "UAC",
        ["Security.DiagUserType"] = "Kontotyp",
        ["Security.DiagExecutionMode"] = "Körläge för säkerhet",
        ["Security.DiagLastScan"] = "Senaste kontroll",
        ["Security.DiagNeverScanned"] = "Inte körd ännu",
        ["Security.DiagPackaged"] = "Paketidentitet",
        ["Security.DiagElevated"] = "Körs med utökad behörighet",
        ["Security.DiagYes"] = "Ja",
        ["Security.DiagNo"] = "Nej",
        ["Security.Rescan"] = "Kör kontroll igen",
        ["Security.Scanning"] = "Kontrollerar...",
        ["Security.AuditOnlyNote"] =
            "KidShell körs i granskningsläge. Inställningar läses men ändras aldrig.",

        // Parent overview summary
        ["Overview.SecurityDevelopment"] = "Utvecklingsläge",
        ["Overview.SecurityReady"] = "Redo att konfigureras",
        ["Overview.SecurityReadyWithWarnings"] = "Redo med anmärkningar",
        ["Overview.SecurityNotReady"] = "Inte redo",

        // ---------- Dialogs ----------
        ["Dialog.DiscardTitle"] = "Osparade ändringar",
        ["Dialog.DiscardBody"] = "Du har ändringar som inte är sparade. Vill du kasta dem?",
        ["Dialog.DiscardPrimary"] = "Kasta ändringar",
        ["Dialog.DiscardSecondary"] = "Spara och gå tillbaka",
        ["Dialog.Cancel"] = "Avbryt",
        ["Dialog.ExitTitle"] = "Avsluta KidShell?",
        ["Dialog.ExitBodyDeveloper"] =
            "I utvecklingsläge stänger den här knappen bara KidShell. Ingen Windows-användare loggas ut.",
        ["Dialog.ExitPrimary"] = "Avsluta KidShell",
        ["Dialog.ChangePinTitle"] = "Ändra PIN-kod",
        ["Dialog.ChangePinBody"] = "Ange en ny sexsiffrig PIN-kod.",
        ["Dialog.ChangePinConfirm"] = "Upprepa PIN-koden",
        ["Dialog.ChangePinMismatch"] = "PIN-koderna är inte lika.",
        ["Dialog.ChangePinInvalid"] = "PIN-koden måste vara sex siffror.",
        ["Dialog.ChangePinSaved"] = "PIN-koden är uppdaterad.",
        ["Dialog.Ok"] = "OK",

        // ---------- Status strip ----------
        ["Status.BatteryAutomation"] = "Batteri {0} procent",
        ["Status.NetworkOnline"] = "Ansluten till nätverk",
        ["Status.NetworkOffline"] = "Inget nätverk",
        ["Status.ClockAutomation"] = "Klockan är {0}",

        // ---------- Config recovery ----------
        ["Config.RecoveredTitle"] = "Inställningarna återställdes",
        ["Config.RecoveredBody"] =
            "Inställningsfilen gick inte att läsa, så KidShell startade med standardinställningar. Den gamla filen har sparats för felsökning.",

        // ---------- Developer ----------
        ["Dev.Badge"] = "Utvecklingsläge",
        ["Dev.BadgeOpenPin"] = "Utvecklingsläge · standard-PIN",
        ["Dev.ParentShortcut"] = "Ctrl+Skift+P öppnar PIN-rutan",
    };

    public static string Get(string key) =>
        Swedish.TryGetValue(key, out var value) ? value : key;

    /// <summary>
    /// Swedish genitive of a name: "Alva" becomes "Alvas", but a name that
    /// already ends in s, x or z takes no extra -s ("Lucas dator", not
    /// "Lucass dator").
    /// </summary>
    public static string Genitive(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return char.ToLowerInvariant(trimmed[^1]) is 's' or 'x' or 'z'
            ? trimmed
            : trimmed + "s";
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
