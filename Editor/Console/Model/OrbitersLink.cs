using System;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// How the Mixer console reaches Orbiters: the server address, the signed-in member and a way to sign in. Without Orbiters
    /// Toolkit it is the production server and nobody is signed in; with it, the Toolkit fills these in (its server, its
    /// account, its sign-in buttons) and raises <see cref="Changed"/> when they change.
    /// </summary>
    internal static class OrbitersLink
    {
        /// <summary>The full address of an API path on the server in use ("logger/explanations").</summary>
        internal static Func<string, string> ApiUrl = path => "https://api.orbiters.cc/" + path.TrimStart('/');

        /// <summary>The full address of a page of the Orbiters website ("legal/ai").</summary>
        internal static Func<string, string> SiteUrl = path => "https://orbiters.cc/" + path.TrimStart('/');

        /// <summary>The signed-in member's token, or null.</summary>
        internal static Func<string> Token = () => null;

        /// <summary>The signed-in member's name, or null.</summary>
        internal static Func<string> Username = () => null;

        /// <summary>
        /// Builds the sign-in buttons, calling back once signed in; null when the Mixer console can't sign in (no Orbiters
        /// Toolkit).
        /// </summary>
        internal static Func<string, Action, VisualElement> SignInElement;

        /// <summary>The account or the server changed.</summary>
        internal static event Action Changed;

        internal static bool SignedIn => !string.IsNullOrEmpty(SafeToken());

        internal static bool CanSignIn => SignInElement != null;

        internal static void RaiseChanged() => Changed?.Invoke();

        internal static string SafeToken()
        {
            try
            {
                return Token?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string SafeUsername()
        {
            try
            {
                return Username?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
