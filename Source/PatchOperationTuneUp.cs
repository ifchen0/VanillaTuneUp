using System.Collections.Generic;
using System.Xml;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Runs its operations only for a module that is in use: never while the standalone mod it replaces is
    /// active or something it needs is missing, and, unless <see cref="evenWhenOff"/> is set, only while the
    /// module is switched on. Mod classes (and so the settings) are created before XML patches run.
    /// </summary>
    public class PatchOperationTuneUp : PatchOperation
    {
        public string module;

        /// <summary>Also apply while the module is switched off, e.g. a comp that keeps the module's save data.</summary>
        public bool evenWhenOff;

        public List<PatchOperation> operations = new List<PatchOperation>();

        protected override bool ApplyWorker(XmlDocument xml)
        {
            TuneUpModule m = Modules.Get(module);
            if (m == null)
            {
                Log.Error($"[Vanilla Tune-Up] Unknown module '{module}' in an XML patch.");
                return false;
            }
            if (m.ActiveLegacyMod() != null || !m.IsAvailable())
                return true;
            if (!evenWhenOff && !VanillaTuneUpMod.Settings.IsEnabled(m))
                return true;
            foreach (PatchOperation operation in operations)
            {
                if (!operation.Apply(xml))
                    return false;
            }
            return true;
        }
    }
}
