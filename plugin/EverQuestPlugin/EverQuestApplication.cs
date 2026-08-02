namespace Loupedeck.EverQuestPlugin
{
    using System;

    // The plugin loader requires exactly one ClientApplication class in the assembly,
    // even for an API-only plugin (HasNoApplication = true). It is never activated.
    public class EverQuestApplication : ClientApplication
    {
        protected override String GetProcessName() => "eqgame";

        protected override String GetBundleName() => "";
    }
}
