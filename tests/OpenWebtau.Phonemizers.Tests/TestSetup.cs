using System.Runtime.CompilerServices;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenWebtau.Phonemizers;

namespace OpenWebtau.Phonemizers.Tests;

static class TestSetup {
    // Program.cs registers the built-in phonemizers the same way, since the browser cannot load
    // OpenUtau.Plugin.Builtin.dll off disk. character.yaml names phonemizers by full type name.
    [ModuleInitializer]
    internal static void RegisterPhonemizers() {
        foreach (var type in typeof(OpenUtau.Plugin.Builtin.ArpasingPhonemizer).Assembly.GetExportedTypes()) {
            if (!type.IsAbstract && type.IsSubclassOf(typeof(Phonemizer))) PhonemizerFactory.Get(type);
        }
        PhonemizerFactory.Get(typeof(DefaultPhonemizer));
        PhonemizerFactory.Get(typeof(AutoPhonemizer));
        PhonemizerFactory.BuildList();
    }
}
