using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Before any Git test: the suite's tests call Mixer Git's synchronous changes on the editor thread (the product never
    // does: there they are refused), and every exclusive command outside an operation's lease is recorded (checked by
    // UnitGitTestRepo at each Dispose).
    [SetUpFixture]
    public sealed class UnitGitTestSetup
    {
        [OneTimeSetUp]
        public void BeforeEveryGitTest()
        {
            UnitGitLease.EditorThreadAllowedForTests = true;
            UnitGitLease.RecordUnleased = true;
        }
    }
}
