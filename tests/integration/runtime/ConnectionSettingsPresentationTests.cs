using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class ConnectionSettingsPresentationTests
    {
        internal static int Run()
        {
            Program.Stage = "connection view languages and client stdio fields";
            int checks = 0;
            string environment = "probe-ui-" + Guid.NewGuid().ToString("N").Substring(0, 20);
            var configuration = ClientLaunchConfiguration.Create(environment.ToUpperInvariant(), "Local.With_Id");
            string root = Path.GetDirectoryName(Path.GetDirectoryName(configuration.Command))!;
            Program.Assert(!Directory.Exists(root), "client field fixture environment starts absent"); checks++;
            Program.Assert(configuration.Command == Path.Combine(root, "launcher", "SkylinesLink.Launcher.exe") &&
                configuration.Arguments.SequenceEqual(new[] { "--environment", environment, "--profile", "Local.With_Id" }),
                "client fields follow the actual fixed launcher and argv contract, with canonical environment and exact profile identity"); checks++;
            bool immutable = false;
            try { ((IList<string>)configuration.Arguments)[3] = "other"; }
            catch (NotSupportedException) { immutable = true; }
            Program.Assert(immutable, "a view consumer cannot change the profile behind a created client configuration"); checks++;
            foreach (string locale in new[] { "en-US", "zh-HANS" })
            {
                string text = ConnectionSettingsText.ClientFields(configuration, locale);
                string[] lines = text.Split('\n');
                Program.Assert(lines.Length == 4 && lines[0].Length > 0 && lines[1].EndsWith("stdio", StringComparison.Ordinal) &&
                    lines[2].Substring(lines[2].IndexOf(':') + 2) == configuration.Command &&
                    JArray.Parse(lines[3].Substring(lines[3].IndexOf(':') + 2)).Select(value => (string)value!).SequenceEqual(configuration.Arguments),
                    "each language presents a draft warning, unchanged executable field and JSON argv rather than a shell command"); checks++;
            }
            Program.Assert(!Directory.Exists(root), "view creation and formatting create no environment, authorization or installation"); checks++;
            foreach (var invalid in new[] { (Environment: "default", Profile: "a b"), (Environment: "default", Profile: "a."),
                (Environment: "default", Profile: "--profile\""), (Environment: "default.", Profile: "local") })
            {
                bool rejected = false;
                try { ClientLaunchConfiguration.Create(invalid.Environment, invalid.Profile); }
                catch (ArgumentException) { rejected = true; }
                Program.Assert(rejected, "invalid draft identifiers do not produce client setup fields"); checks++;
            }

            string english = ConnectionSettingsText.Format(ConnectionNotice.ServiceRunning, "en-US");
            string chinese = ConnectionSettingsText.Format(ConnectionNotice.ServiceRunning, "zh-HANS");
            Program.Assert(english != chinese && chinese.Any(character => character >= '\u4e00' && character <= '\u9fff') &&
                ConnectionSettingsText.Format(ConnectionNotice.ServiceRunning, "fr-FR") == english &&
                ConnectionSettingsText.Format(ConnectionNotice.ServiceRunning, "ZH-hans") == chinese,
                "the same service fact follows the chosen supported language and uses English fallback"); checks++;
            Program.Assert(ConnectionSettingsText.PreparationFailure(new SetupInitializationResult("CHILD_EXITED", 37, PreparedInstallationState.MatchesSource)) ==
                ConnectionNotice.PrepareVerifiedIncomplete &&
                ConnectionSettingsText.PreparationFailure(new SetupInitializationResult("TIMEOUT", null, PreparedInstallationState.DifferentCore)) ==
                ConnectionNotice.PrepareDifferentCore &&
                ConnectionSettingsText.PreparationFailure(new SetupInitializationResult("CANCELLED", null, PreparedInstallationState.MatchesSource)) ==
                ConnectionNotice.PrepareCancelled,
                "localized failure projection preserves publication facts and explicit cancellation instead of suggesting universal reinstall"); checks++;
            return checks;
        }
    }
}
