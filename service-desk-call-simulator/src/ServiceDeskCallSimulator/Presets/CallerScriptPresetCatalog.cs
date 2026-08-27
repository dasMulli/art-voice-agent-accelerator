using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Presets;

public static class CallerScriptPresetCatalog
{
    public static IReadOnlyList<CallerScriptPreset> CreateDefaultPresets(SimulatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var english = settings.Speech.English;
        var german = settings.Speech.German;
        var polish = settings.Speech.Polish;

        return
        [
            CreatePreset(
                "[EN] Printer not working",
                english.RecognitionLocale,
                english.Voice,
                "Hello, this is Maya from the operations desk. Our office printer has stopped responding.",
                "Maya",
                "The front-office printer was working this morning and now shows an offline error.",
                "Need a quick status check and support escalation.",
                "High",
                "+14155550101",
                "Please call back on the same number once the printer queue is cleared."),
            CreatePreset(
                "[DE] Drucker funktioniert nicht",
                german.RecognitionLocale,
                german.Voice,
                "Hallo, hier spricht Maya vom Empfang. Unser Bürodrucker reagiert nicht mehr.",
                "Maya",
                "Der Drucker an der Anmeldung war heute Morgen noch in Betrieb und zeigt jetzt einen Offline-Fehler.",
                "Wir brauchen eine schnelle Prüfung und eine Eskalation an den Support.",
                "Hoch",
                "+4915112345678",
                "Bitte rufen Sie unter derselben Nummer zurück, sobald die Druckerwarteschlange freigegeben ist."),
            CreatePreset(
                "[EN] VPN access",
                english.RecognitionLocale,
                english.Voice,
                "Hello, this is Liam from consulting. I cannot connect to the VPN this morning.",
                "Liam",
                "Remote work started and the VPN client rejects the sign-in on a laptop used for client work.",
                "I need access restored before the first meeting starts.",
                "Medium",
                "+14155550102",
                "A callback before 9 a.m. would keep the day on schedule."),
            CreatePreset(
                "[DE] VPN-Zugriff",
                german.RecognitionLocale,
                german.Voice,
                "Hallo, hier spricht Liam aus der Beratung. Ich kann mich heute Morgen nicht mit dem VPN verbinden.",
                "Liam",
                "Der Arbeitstag hat begonnen und der VPN-Client verweigert die Anmeldung auf einem Laptop für Kundentermine.",
                "Ich brauche den Zugriff vor dem ersten Termin zurück.",
                "Mittel",
                "+4915112345679",
                "Ein Rückruf vor 9 Uhr würde den Tagesplan sichern."),
            CreatePreset(
                "[EN] Email outage",
                english.RecognitionLocale,
                english.Voice,
                "Hello, this is Sofia from sales. Email delivery has stopped for my team.",
                "Sofia",
                "The sales inbox stopped sending and receiving messages after a mailbox move.",
                "We need to restore message flow and confirm no mail was lost.",
                "High",
                "+14155550103",
                "The team is waiting on customer replies and contract notices."),
            CreatePreset(
                "[DE] E-Mail-Ausfall",
                german.RecognitionLocale,
                german.Voice,
                "Hallo, hier spricht Sofia aus dem Vertrieb. Der E-Mail-Versand und -Empfang für mein Team ist ausgefallen.",
                "Sofia",
                "Nach einer Postfachverschiebung kommen im Vertriebs-Posteingang keine Nachrichten mehr an und gehen auch nicht hinaus.",
                "Wir müssen den Nachrichtenfluss wiederherstellen und prüfen, ob Mails verloren gegangen sind.",
                "Hoch",
                "+4915112345680",
                "Das Team wartet auf Kundenantworten und Vertragsmeldungen."),
            CreatePreset(
                "[EN] Payroll question",
                english.RecognitionLocale,
                english.Voice,
                "Hello, this is Daniel from logistics. I have a question about my payroll statement.",
                "Daniel",
                "The latest payslip shows an unexpected deduction after a shift pattern change.",
                "I need a clarification before the next payroll cutoff.",
                "Low",
                "+14155550104",
                "A written note is fine if a callback is not possible today."),
            CreatePreset(
                "[DE] Frage zur Gehaltsabrechnung",
                german.RecognitionLocale,
                german.Voice,
                "Hallo, hier spricht Daniel aus der Logistik. Ich habe eine Frage zu meiner Gehaltsabrechnung.",
                "Daniel",
                "Auf der letzten Abrechnung erscheint nach einer Schichtänderung ein unerwarteter Abzug.",
                "Ich brauche eine Klärung vor dem nächsten Abrechnungslauf.",
                "Niedrig",
                "+4915112345681",
                "Eine kurze schriftliche Rückmeldung ist auch in Ordnung, falls heute kein Rückruf möglich ist."),

            // Showcase preset: the caller opens in German and switches to Polish deterministically
            // after the first finalized service-desk turn. The switch is a declared preset fact and
            // never inferred from the free-text details below.
            CreatePreset(
                "[DE→PL] Netzwerkstörung / awaria sieci",
                german.RecognitionLocale,
                german.Voice,
                "Guten Tag, hier spricht Ewa vom Standort Wien. Unser Standortnetz ist ausgefallen.",
                "Ewa",
                "Am Standort Wien sind seit heute Morgen alle Netzwerkverbindungen unterbrochen; die Kollegin spricht Deutsch und Polnisch.",
                "Wir brauchen eine Störungsmeldung und eine Eskalation an den Netzbetrieb.",
                "Hoch",
                "+4915112345682",
                "Ein Rückruf ist jederzeit möglich, solange die Mobilfunkverbindung steht.",
                new CallerLanguageSwitchPolicy
                {
                    TargetLocale = polish.RecognitionLocale,
                    TargetVoice = polish.Voice,
                    AfterFinalServiceDeskTurns = 1,
                }),
        ];
    }

    private static CallerScriptPreset CreatePreset(
        string name,
        string locale,
        string voice,
        string openingLine,
        string identity,
        string background,
        string reason,
        string urgency,
        string callbackNumber,
        string additionalDetails,
        CallerLanguageSwitchPolicy? languageSwitch = null)
    {
        E164PhoneNumber.EnsureValid(callbackNumber, nameof(callbackNumber));

        return new CallerScriptPreset
        {
            Name = name,
            Locale = locale,
            Voice = voice,
            OpeningLine = openingLine,
            Identity = identity,
            Background = background,
            Reason = reason,
            Urgency = urgency,
            CallbackNumber = callbackNumber,
            AdditionalDetails = additionalDetails,
            LanguageSwitch = languageSwitch?.Validated(nameof(languageSwitch)),
        };
    }
}
