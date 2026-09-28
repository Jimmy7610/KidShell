namespace KidShell.Core.Security;

/// <summary>Which KidShell configuration a row is being judged against.</summary>
public enum ProtectionMode
{
    /// <summary>
    /// KidShell installed, nothing about Windows changed. What every build so
    /// far actually is.
    /// </summary>
    AppOnly = 0,

    /// <summary>
    /// A standard child account, KidShell at startup, app control where it can
    /// be deployed, browser policy where it can be deployed, watchdog.
    /// Available on every edition, and honest about what it does not cover.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Standard plus Assigned Access as a restricted user experience. Pro,
    /// Enterprise, Education and IoT Enterprise only.
    /// </summary>
    Secure = 2
}

/// <summary>
/// How well one escape route is covered.
///
/// The values are deliberately awkward. A two-state "protected / not
/// protected" would force every honest "it depends" into one bucket or the
/// other, and the bucket it would land in is the flattering one.
/// </summary>
public enum ProtectionLevel
{
    /// <summary>Verified blocked, by reading the state back.</summary>
    Protected = 0,

    /// <summary>Made harder, but not prevented. Never presented as protection.</summary>
    Mitigated = 1,

    /// <summary>Works. The child can do this.</summary>
    NotProtected = 2,

    /// <summary>Would be covered once Standard Mode is applied and verified.</summary>
    RequiresStandard = 3,

    /// <summary>Needs Assigned Access, so needs Pro or above.</summary>
    RequiresSecure = 4,

    /// <summary>
    /// Cannot be judged without a dedicated machine. Distinct from "not
    /// protected": the answer is unknown, and claiming either would be a guess.
    /// </summary>
    RequiresDeviceTest = 5,

    /// <summary>
    /// The OS owns this and no application can intercept it. Saying so is more
    /// useful than a status that implies a future fix.
    /// </summary>
    CannotBeProtected = 6,

    /// <summary>The route does not apply to this configuration.</summary>
    NotApplicable = 7
}

/// <summary>One row of the escape matrix.</summary>
public sealed record EscapeRoute
{
    public required int Number { get; init; }

    /// <summary>What a person would try, in Swedish.</summary>
    public required string Name { get; init; }

    /// <summary>What happens today, with no Windows changes applied.</summary>
    public required ProtectionLevel AppOnly { get; init; }

    /// <summary>What Standard Mode would do, once applied and verified.</summary>
    public required ProtectionLevel Standard { get; init; }

    /// <summary>What Secure Mode would do, on an edition that supports it.</summary>
    public required ProtectionLevel Secure { get; init; }

    /// <summary>Why, in a sentence. Never a status on its own.</summary>
    public required string Note { get; init; }

    /// <summary>
    /// Whether this row's claim has been verified on real hardware.
    ///
    /// False for every row until a dedicated device has run the matrix, and
    /// the document says so rather than implying the table is a test result.
    /// </summary>
    public bool VerifiedOnDevice { get; init; }

    public ProtectionLevel For(ProtectionMode mode) => mode switch
    {
        ProtectionMode.AppOnly => AppOnly,
        ProtectionMode.Standard => Standard,
        ProtectionMode.Secure => Secure,
        _ => ProtectionLevel.NotProtected
    };
}

/// <summary>
/// The escape-test matrix: every way out of KidShell that anybody has thought
/// of, and an honest answer for each.
///
/// WHY THIS IS CODE AND NOT A TABLE IN A DOCUMENT
/// ----------------------------------------------
/// A markdown table drifts. It gets a green tick when a feature is written
/// rather than when it is verified, and nobody notices because nothing checks
/// it. Here the rows are data, tests assert the rules that keep them honest -
/// a row cannot claim Protected in a mode that has not been verified on a
/// device, Secure cannot be weaker than Standard, every row needs a reason -
/// and SECURITY.md is generated from it.
///
/// The uncomfortable number is the point: on a machine with no Windows changes
/// applied, most of these are simply not protected, and a parent is better off
/// reading that here than discovering it.
/// </summary>
public static class EscapeMatrix
{
    public static IReadOnlyList<EscapeRoute> Routes { get; } = Build();

    private static IReadOnlyList<EscapeRoute> Build() =>
    [
        new()
        {
            Number = 1,
            Name = "Windows-tangenten",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.NotProtected,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Öppnar Start. Bara Windows begränsade läge byter ut skalet."
        },
        new()
        {
            Number = 2,
            Name = "Alt+Tab",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.NotProtected,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Byter fönster. Kräver begränsat läge för att försvinna."
        },
        new()
        {
            Number = 3,
            Name = "Alt+F4",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.Mitigated,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Stänger KidShell. Vakttjänsten startar om det, men fönstret hinner försvinna."
        },
        new()
        {
            Number = 4,
            Name = "Ctrl+Skift+Esc (Aktivitetshanteraren)",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.NotProtected,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Begränsat läge döljer Aktivitetshanteraren för standardkonton."
        },
        new()
        {
            Number = 5,
            Name = "Win+R (Kör)",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Kör-rutan startar program. Appkontroll avgör vad som faktiskt får starta."
        },
        new()
        {
            Number = 6,
            Name = "Win+X",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.NotProtected,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Snabbmenyn med systemverktyg. Kräver begränsat läge."
        },
        new()
        {
            Number = 7,
            Name = "Ctrl+Alt+Delete",
            AppOnly = ProtectionLevel.CannotBeProtected,
            Standard = ProtectionLevel.CannotBeProtected,
            Secure = ProtectionLevel.CannotBeProtected,
            Note = "Windows äger den här tangentkombinationen. Ingen app kan fånga den, " +
                   "och en produkt som påstår sig göra det har fel."
        },
        new()
        {
            Number = 8,
            Name = "Öppna-dialogen i ett program",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "En filbläddrare inuti ett tillåtet program. Markerad per app i appprofilen."
        },
        new()
        {
            Number = 9,
            Name = "Spara som-dialogen",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Samma sak som Öppna-dialogen."
        },
        new()
        {
            Number = 10,
            Name = "\"Öppna mappen som innehåller filen\"",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Startar Utforskaren. Appkontroll avgör om den får starta."
        },
        new()
        {
            Number = 11,
            Name = "ShellExecute från ett tillåtet program",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Ett program kan be Windows starta ett annat. Bara appkontroll stoppar det."
        },
        new()
        {
            Number = 12,
            Name = "Egna protokollhanterare (t.ex. ms-settings:)",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "En länk kan öppna Inställningar. Appprofilerna registrerar vilka protokoll " +
                   "varje program kan använda."
        },
        new()
        {
            Number = 13,
            Name = "Omdirigering i webbläsaren",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresStandard,
            Note = "Kräver att webbläsarpolicyn går att installera på datorn. Gäller bara Edge."
        },
        new()
        {
            Number = 14,
            Name = "Nedladdningar",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresStandard,
            Note = "Edge-policyn kan begränsa nedladdningar. En nedladdad fil kan ändå inte " +
                   "startas om appkontroll gäller."
        },
        new()
        {
            Number = 15,
            Name = "USB-minne",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresStandard,
            Note = "Appkontroll gäller även program på ett USB-minne, men filerna går att läsa."
        },
        new()
        {
            Number = 16,
            Name = "Genvägar (.lnk) till annat",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "En genväg är bara en pekare. Det som avgör är om målet får starta."
        },
        new()
        {
            Number = 17,
            Name = "URL-hanterare",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Samma mekanism som protokollhanterare."
        },
        new()
        {
            Number = 18,
            Name = "Underprocesser från ett startprogram",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Att tillåta ett startprogram säger ingenting om vad det sedan startar. " +
                   "Appprofilerna registrerar underprocesserna."
        },
        new()
        {
            Number = 19,
            Name = "Ett programs egen uppdaterare",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Uppdaterare startar egna processer. Profilerna registrerar dem så att " +
                   "reglerna kan ta hänsyn till dem."
        },
        new()
        {
            Number = 20,
            Name = "KidShell kraschar",
            AppOnly = ProtectionLevel.Mitigated,
            Standard = ProtectionLevel.Mitigated,
            Secure = ProtectionLevel.RequiresDeviceTest,
            Note = "Kraschslinga upptäcks och visar en lugn skärm i stället för att blinka. " +
                   "Vad barnet ser i begränsat läge måste provas på en riktig dator."
        },
        new()
        {
            Number = 21,
            Name = "Vakttjänsten kraschar eller stoppas",
            AppOnly = ProtectionLevel.NotApplicable,
            Standard = ProtectionLevel.RequiresDeviceTest,
            Secure = ProtectionLevel.RequiresDeviceTest,
            Note = "Tjänsten körs som LocalSystem och kan inte stoppas av ett standardkonto, " +
                   "men beteendet måste provas."
        },
        new()
        {
            Number = 22,
            Name = "Starta om datorn",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresDeviceTest,
            Secure = ProtectionLevel.RequiresDeviceTest,
            Note = "Kräver att autostart och barnkontot fungerar efter omstart. Måste provas."
        },
        new()
        {
            Number = 23,
            Name = "Viloläge och återupptagning",
            AppOnly = ProtectionLevel.Protected,
            Standard = ProtectionLevel.Protected,
            Secure = ProtectionLevel.Protected,
            Note = "Skärmtiden räknar inte sovtid. Att sova förbrukar varken dagens tid " +
                   "eller ger extra.",
            VerifiedOnDevice = true
        },
        new()
        {
            Number = 24,
            Name = "Ställa tillbaka klockan",
            AppOnly = ProtectionLevel.Mitigated,
            Standard = ProtectionLevel.Mitigated,
            Secure = ProtectionLevel.Mitigated,
            Note = "Tiden räknas från en klocka som inte går att ställa, så ingen tid " +
                   "återbetalas. Bakåthopp registreras och visas, men KidShell gör ingen " +
                   "kapprustning av det.",
            VerifiedOnDevice = true
        },
        new()
        {
            Number = 25,
            Name = "Windows Update startar om datorn",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresDeviceTest,
            Secure = ProtectionLevel.RequiresDeviceTest,
            Note = "Samma som omstart, men vid en tidpunkt ingen valt. Måste provas."
        },
        new()
        {
            Number = 26,
            Name = "Avinstallera KidShell",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Ett standardkonto kan inte avinstallera ett program som installerats " +
                   "för alla användare."
        },
        new()
        {
            Number = 27,
            Name = "Redigera KidShells inställningsfil",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.RequiresStandard,
            Secure = ProtectionLevel.RequiresSecure,
            Note = "Filen ligger i barnets egen profil. PIN-koden är hashad, men " +
                   "inställningarna går att ändra utan appkontroll."
        },
        new()
        {
            Number = 28,
            Name = "Starta i felsäkert läge",
            AppOnly = ProtectionLevel.NotProtected,
            Standard = ProtectionLevel.NotProtected,
            Secure = ProtectionLevel.RequiresDeviceTest,
            Note = "Felsäkert läge startar inte tredjepartstjänster. Vad som gäller där " +
                   "måste provas på en riktig dator."
        },
        new()
        {
            Number = 29,
            Name = "Starta från USB eller återställningsmedia",
            AppOnly = ProtectionLevel.CannotBeProtected,
            Standard = ProtectionLevel.CannotBeProtected,
            Secure = ProtectionLevel.CannotBeProtected,
            Note = "Den som kan starta ett annat operativsystem äger datorn. Det ligger " +
                   "utanför vad en app kan göra något åt."
        }
    ];

    /// <summary>How many routes fall in a given level for a mode.</summary>
    public static int Count(ProtectionMode mode, ProtectionLevel level) =>
        Routes.Count(r => r.For(mode) == level);

    /// <summary>
    /// The honest headline for a mode: how many routes are genuinely covered
    /// out of how many are known.
    /// </summary>
    public static string Summarize(ProtectionMode mode)
    {
        var total = Routes.Count;
        var covered = Count(mode, ProtectionLevel.Protected);
        var cannot = Count(mode, ProtectionLevel.CannotBeProtected);
        var untested = Count(mode, ProtectionLevel.RequiresDeviceTest);

        return mode switch
        {
            ProtectionMode.AppOnly =>
                $"{covered} av {total} vägar är spärrade. Windows är inte låst på den här datorn.",

            _ => $"{covered} av {total} vägar är bekräftat spärrade, {cannot} kan inte spärras " +
                 $"av någon app, och {untested} måste provas på en riktig dator."
        };
    }

    public static string Describe(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Protected => "Skyddad",
        ProtectionLevel.Mitigated => "Försvårad",
        ProtectionLevel.NotProtected => "Inte skyddad",
        ProtectionLevel.RequiresStandard => "Kräver Standardläge",
        ProtectionLevel.RequiresSecure => "Kräver Säkert läge",
        ProtectionLevel.RequiresDeviceTest => "Måste provas på en dedikerad dator",
        ProtectionLevel.CannotBeProtected => "Kan inte spärras",
        ProtectionLevel.NotApplicable => "Gäller inte",
        _ => "Okänt"
    };
}
