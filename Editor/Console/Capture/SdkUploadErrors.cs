using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Where a VRChat upload failed. The SDK's avatar and world builders catch what goes wrong during an upload, wrap it
    /// (<c>new UploadException(e.Message, e)</c>) and show and log only its message ("Upload Failed: Object reference not
    /// set to an instance of an object"): the stack that says where it happened is lost (Unity's Mono never raises
    /// first-chance exceptions to catch it earlier). A Harmony prefix on the builders' private <c>HandleUploadError</c>,
    /// which every failure goes through, logs it once as an error with the whole exception (inner ones too), and that log
    /// is what "Check Console for more details" shows. Found by name through reflection, Harmony included (it ships with
    /// the VRChat SDK): without the SDK, Harmony or the method, nothing is hooked.
    /// </summary>
    [InitializeOnLoad]
    internal static class SdkUploadErrors
    {
        /// <summary>How the log starts (what <see cref="LoggerWindow.ErrorToShow"/> looks for).</summary>
        internal const string Prefix = "VRChat's SDK only shows the message; where it happened: ";

        private const string HarmonyId = "cc.orbiters.mixer.console.upload-errors";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] Builders =
        {
            "VRC.SDK3A.Editor.VRCSdkControlPanelAvatarBuilder",
            "VRC.SDK3.Editor.VRCSdkControlPanelWorldBuilder",
        };

        // Exceptions already logged: the same one goes through the builder more than once (rethrown and handled again).
        private static readonly ConditionalWeakTable<Exception, object> Logged = new ConditionalWeakTable<Exception, object>();

        static SdkUploadErrors()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            try
            {
                Hook();
            }
            catch (Exception)
            {
                // Another Harmony or SDK than this was written for: uploads keep the SDK's own message only.
            }
        }

        /// <summary>The builders' methods that are hooked now (tests and diagnostics).</summary>
        internal static int Hooked { get; private set; }

        private static void Hook()
        {
            var harmonyType = TypeNamed("HarmonyLib.Harmony", "0Harmony");
            var methodType = TypeNamed("HarmonyLib.HarmonyMethod", "0Harmony");
            if (harmonyType == null || methodType == null)
            {
                return;
            }

            var prefix = typeof(SdkUploadErrors).GetMethod(nameof(BeforeUploadError), BindingFlags.Static | BindingFlags.NonPublic);
            var harmonyMethod = methodType.GetConstructor(new[] { typeof(MethodInfo) })?.Invoke(new object[] { prefix });
            var patch = harmonyType.GetMethods().FirstOrDefault(m => m.Name == "Patch" && m.GetParameters().Length >= 2 && m.GetParameters()[0].ParameterType == typeof(MethodBase));
            if (harmonyMethod == null || patch == null)
            {
                return;
            }

            object harmony = null;
            foreach (string builderName in Builders)
            {
                var builder = TypeNamed(builderName, null);
                var original = builder?.GetMethod("HandleUploadError", Any, null, new[] { typeof(Exception) }, null);
                if (original == null || original.GetParameters()[0].Name != "exception")
                {
                    continue;
                }

                harmony = harmony ?? Activator.CreateInstance(harmonyType, HarmonyId);
                var arguments = new object[patch.GetParameters().Length];
                arguments[0] = original;
                arguments[1] = harmonyMethod;
                patch.Invoke(harmony, arguments);
                Hooked++;
            }
        }

        // Harmony's prefix: before the builder handles the failure (its argument is named as the original's).
        private static void BeforeUploadError(Exception exception)
        {
            try
            {
                Report(exception);
            }
            catch (Exception)
            {
                // Never in the way of the SDK's own handling.
            }
        }

        /// <summary>Logs where <paramref name="exception"/> happened, once; false when it says nothing more than its message.</summary>
        internal static bool Report(Exception exception)
        {
            string text = Describe(exception);
            if (text == null || exception == null || Logged.TryGetValue(exception, out _))
            {
                return false;
            }

            Logged.Add(exception, null);
            // The text holds the exception's own stack: Unity's (this hook's) would only mislead.
            Debug.LogFormat(LogType.Error, LogOption.NoStacktrace, null, "{0}", text);
            return true;
        }

        /// <summary>
        /// The log for a failure: the whole exception (the inner one where it happened, with its stack), or null when no
        /// exception in it has a stack.
        /// </summary>
        internal static string Describe(Exception exception)
        {
            Exception withStack = null;
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (!string.IsNullOrEmpty(e.StackTrace))
                {
                    withStack = e;
                }
            }

            return withStack == null ? null : Prefix + exception.ToString();
        }

        private static Type TypeNamed(string fullName, string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assemblyName != null && assembly.GetName().Name != assemblyName)
                {
                    continue;
                }

                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
