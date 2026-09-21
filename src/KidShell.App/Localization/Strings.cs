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
        ["Overview.Subtitle"] = "Så här är {0}s dator inställd just nu.",
        ["Overview.AppsTitle"] = "Appar",
        ["Overview.AppsValue"] = "{0} tillåtna",
        ["Overview.AppsHint"] = "Av totalt {0} konfigurerade.",
        ["Overview.ScreenTimeTitle"] = "Skärmtid",
        ["Overview.ScreenTimeNoLimit"] = "Ingen gräns i MVP 0.1",
        ["Overview.ScreenTimeConfigured"] = "{0} min vardag · {1} min helg",
        ["Overview.ScreenTimeHint"] = "Inställningen sparas men används inte ännu.",
        ["Overview.WebTitle"] = "Webbfilter",
        ["Overview.WebNotConfigured"] = "Inte konfigurerat",
        ["Overview.WebHint"] = "Valet sparas lokalt. Ingen webbläsare styrs ännu.",
        ["Overview.SecurityTitle"] = "Säkerhet",
        ["Overview.SecurityValue"] = "Utvecklingsläge",
        ["Overview.SecurityHint"] = "Windows är inte låst.",
        ["Overview.BannerTitle"] = "MVP 0.1 låser inte Windows",
        ["Overview.BannerBody"] =
            "Den här versionen är ett visuellt och arkitekturellt skal. Barnet kan fortfarande minimera KidShell och nå resten av Windows. Riktig låsning kommer i en senare milstolpe.",

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
        ["ScreenTime.NoticeTitle"] = "Konfiguration finns – kontroll saknas",
        ["ScreenTime.NoticeBody"] =
            "KidShell sparar de här tiderna men avbryter ingenting ännu. Tidskontrollen kräver bakgrundsövervakning som kommer i en senare milstolpe.",

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
        ["Profile.ThemeMeadow"] = "Äng",
        ["Profile.ThemeSunset"] = "Solnedgång",
        ["Profile.ThemeOcean"] = "Hav",
        ["Profile.PreviewTitle"] = "Så här ser det ut",

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
        ["Dev.ParentShortcut"] = "Ctrl+Skift+P öppnar PIN-rutan",
    };

    public static string Get(string key) =>
        Swedish.TryGetValue(key, out var value) ? value : key;

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
