using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Common;

namespace HarfBuzzFix
{
    public class HarfBuzzFixModSystem : ModSystem
    {
        private const int RTLD_NOW = 2;
        private const int RTLD_DEEPBIND = 0x00008;

        private static bool _isolatedRegistered;
        private static ICoreAPI _api;

        private Harmony _harmony;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartPre(ICoreAPI api)
        {
            base.StartPre(api);
            _api = api;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return;
            }

            try
            {
                Type target = AccessTools.TypeByName("Gui.NativeLibraryLoader");
                if (target == null)
                {
                    api.Logger.Notification("[harfbuzzfix] Gui.NativeLibraryLoader not found (gui not installed?) - nothing to patch.");
                    return;
                }

                MethodInfo registerMethod = AccessTools.Method(target, "Register");
                if (registerMethod == null)
                {
                    api.Logger.Notification("[harfbuzzfix] Gui.NativeLibraryLoader.Register method not found - nothing to patch.");
                    return;
                }

                _harmony = new Harmony("harfbuzzfix.deepbind");
                _harmony.Patch(registerMethod,
                    prefix: new HarmonyMethod(typeof(HarfBuzzFixModSystem), nameof(RegisterPrefix)));

                api.Logger.Notification("[harfbuzzfix] Patched Gui.NativeLibraryLoader.Register - will use RTLD_DEEPBIND isolation instead of gui's default loader.");
            }
            catch (Exception ex)
            {
                api.Logger.Notification("[harfbuzzfix] Failed to patch NativeLibraryLoader, gui will use its own (unisolated) loader: {0}", ex);
            }
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll(_harmony.Id);
            base.Dispose();
        }

        // Runs instead of Gui.NativeLibraryLoader.Register(). Returning false skips
        // the original (unsafe) method body entirely - gui's own registration never runs.
        public static bool RegisterPrefix()
        {
            try
            {
                RegisterIsolatedResolver();
                return false;
            }
            catch (Exception ex)
            {
                // If our replacement fails for any reason, let gui's original method run -
                // that's the pre-fix behavior (works, just not isolated), never worse than before.
                _api?.Logger.Notification("[harfbuzzfix] Isolated registration failed, falling back to gui's default loader: {0}", ex);
                return true;
            }
        }

        private static void RegisterIsolatedResolver()
        {
            if (_isolatedRegistered)
            {
                return;
            }

            Assembly harfBuzzSharp = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "HarfBuzzSharp");

            if (harfBuzzSharp == null)
            {
                throw new InvalidOperationException("HarfBuzzSharp assembly is not loaded yet.");
            }

            string nativeDir = FindNativeDir(harfBuzzSharp);
            if (nativeDir == null)
            {
                throw new InvalidOperationException("Could not locate HarfBuzzSharp's native library directory.");
            }

            NativeLibrary.SetDllImportResolver(harfBuzzSharp, (name, asm, searchPath) => ResolveNativeLibrary(name, nativeDir));

            _isolatedRegistered = true;
        }

        // Loads the requested native library with RTLD_DEEPBIND isolation if it lives in
        // HarfBuzzSharp's own native directory, otherwise falls back to default resolution -
        // the same fallback gui's own (unisolated) loader used.
        private static IntPtr ResolveNativeLibrary(string name, string nativeDir)
        {
            string fileName = (name.StartsWith("lib", StringComparison.Ordinal) ? "" : "lib") + name + ".so";
            string fullPath = Path.Combine(nativeDir, fileName);

            if (File.Exists(fullPath))
            {
                try
                {
                    IntPtr handle = dlopen(fullPath, RTLD_NOW | RTLD_DEEPBIND);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
                catch
                {
                    // fall through
                }
            }

            NativeLibrary.TryLoad(name, out IntPtr fallbackHandle);
            return fallbackHandle;
        }

        private static string FindNativeDir(Assembly asm)
        {
            try
            {
                string asmDir = Path.GetDirectoryName(asm.Location);
                if (string.IsNullOrEmpty(asmDir))
                {
                    return null;
                }

                string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

                string[] candidates =
                {
                    Path.Combine(asmDir, "native", rid, "native"),
                    Path.Combine(asmDir, "runtimes", rid, "native"),
                };

                foreach (string candidate in candidates)
                {
                    if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "libHarfBuzzSharp.so").Length > 0)
                    {
                        return candidate;
                    }
                }

                if (Directory.Exists(asmDir))
                {
                    string[] found = Directory.GetFiles(asmDir, "libHarfBuzzSharp.so", SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        return Path.GetDirectoryName(found[0]);
                    }
                }
            }
            catch
            {
                // fail open
            }

            return null;
        }

        [DllImport("libdl.so.2", SetLastError = true)]
        private static extern IntPtr dlopen(string filename, int flags);
    }
}
