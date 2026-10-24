using System;
using System.IO;
using UnityEditor;

namespace Orbiters.Logger.Editor.Toolkit
{
    /// <summary>
    /// With Orbiters Toolkit installed, the Mixer console talks to the server Orbiters tools use (production or the development
    /// one) as the signed-in member: explanations for members, the AI diagnosis of project reports, and the Toolkit's
    /// sign-in buttons where the Mixer console asks to connect.
    /// </summary>
    [InitializeOnLoad]
    internal static class OrbitersAccountLink
    {
        private static readonly object Gate = new object();
        private static AuthenticationService.AuthData cached;
        private static string cachedKey;

        static OrbitersAccountLink()
        {
            OrbitersLink.ApiUrl = path => OrbitersEnvironment.ApiUrl(path);
            OrbitersLink.SiteUrl = path => OrbitersEnvironment.WebsiteUrl + path.TrimStart('/');
            OrbitersLink.Token = () => Auth()?.token;
            OrbitersLink.Username = () =>
            {
                var auth = Auth();
                return auth == null ? null : string.IsNullOrEmpty(auth.username) ? auth.user : auth.username;
            };
            OrbitersLink.SignInElement = (reason, connected) => new OrbitersSignInElement(connected, reason, "logger");
            AuthenticationService.Changed += Forget;
            AuthenticationService.Changed += OrbitersLink.RaiseChanged;
            OrbitersEnvironment.Changed += Forget;
            OrbitersEnvironment.Changed += OrbitersLink.RaiseChanged;
        }

        // The account file is decrypted once per change, not at every look (the report window asks several times a second).
        // Its write time tells when another project signed in or out.
        private static AuthenticationService.AuthData Auth()
        {
            string path = AuthenticationService.GetAuthFilePath();
            string key;
            try
            {
                key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L);
            }
            catch (Exception)
            {
                key = path;
            }

            lock (Gate)
            {
                if (key != cachedKey)
                {
                    cached = AuthenticationService.GetAuth();
                    cachedKey = key;
                }

                return cached;
            }
        }

        private static void Forget()
        {
            lock (Gate)
            {
                cachedKey = null;
                cached = null;
            }
        }
    }
}
