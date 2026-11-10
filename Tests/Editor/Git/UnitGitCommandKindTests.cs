using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Every command line Mixer Git builds (collected from its call sites: the service, conflicts, backups, releases, the
    // scoped index, shelves, visits, paths and the window's actions), and the reviewer's list of writes, with what each does
    // to the repository: Read (no gate, may be superseded, stopped at once at a reload), Change (never superseded, reload
    // grace, outside the gate: Git's own locks keep it safe beside local changes) or Exclusive (the write gate too).
    public sealed class UnitGitCommandKindTests
    {
        private const UnitGitWriteGate.Kind R = UnitGitWriteGate.Kind.Read, C = UnitGitWriteGate.Kind.Change, X = UnitGitWriteGate.Kind.Exclusive;

        private static readonly (string line, UnitGitWriteGate.Kind kind)[] Commands =
        {
            // Built by Mixer Git.
            ("symbolic-ref --quiet HEAD", R), ("symbolic-ref HEAD refs/heads/main", X), ("rev-parse --verify HEAD", R),
            ("rev-parse HEAD --symbolic-full-name HEAD", R), ("rev-parse --git-path index", R), ("rev-parse --git-common-dir", R),
            ("rev-parse --abbrev-ref @{-1}", R), ("rev-list --topo-order --reverse HEAD", R), ("rev-list --count HEAD..refs/heads/main", R),
            ("status --porcelain=v2 --branch -z", R), ("status --porcelain=v2 -z --untracked-files=normal --ignore-submodules=all", R),
            ("for-each-ref --format=%(objectname) %(refname)", R), ("log -1 --format=%B", R), ("show -s --format=%h%x1f%p%x1f%P x", R),
            ("show --name-status -z --format= x", R), ("ls-tree -r --name-only HEAD", R), ("ls-files -z --others --exclude-standard", R),
            ("ls-files --unmerged --", R), ("diff --no-ext-diff", R), ("diff --cached --quiet", R),
            ("diff-tree -r -z --name-only --no-renames --diff-filter=A HEAD", R), ("cat-file blob x", R), ("cat-file -s x", R),
            ("var GIT_COMMITTER_IDENT", R), ("config --get branch.main.remote", R), ("config --bool --get commit.gpgSign", R),
            ("check-ref-format --branch topic", R), ("check-ignore --stdin -z", R), ("merge-file --diff3 -L mine a b c", R),
            ("remote", R), ("remote get-url origin", R), ("remote add origin url", X), ("remote set-url origin url", X),
            ("stash list", R), ("stash list --format=%H", R), ("stash show --name-status -z stash@{0}", R),
            ("stash", X), ("stash push -m m", X), ("stash push -u -m m", X), ("stash pop", X), ("stash drop stash@{0}", X), ("stash store -m m x", X),
            ("bundle list-heads b", R), ("bundle verify b", R), ("bundle create b --all", C),
            ("fetch --all --prune", C), ("push", C), ("push -u origin main", C), ("clone --bare --quiet b dir", C),
            ("fetch --quiet --update-head-ok b refs/heads/*:refs/heads/*", X), ("pull --ff-only", X),
            ("--literal-pathspecs add -A --pathspec-file-nul --pathspec-from-file=-", X), ("add -A -- .", X), ("add origin", X),
            ("--literal-pathspecs commit -m t --pathspec-file-nul --pathspec-from-file=-", X), ("commit -F -", X), ("commit --amend -F -", X),
            ("commit --no-edit", X), ("commit-tree t -p p -F -", X), ("fast-import --quiet --done", X),
            ("checkout main", X), ("checkout -b topic", X), ("checkout -t origin/topic", X), ("checkout --force main", X),
            ("checkout-index --temp --stage=2 a", X), ("reset -q HEAD", X), ("reset --hard --quiet", X),
            ("restore --source=HEAD --staged --worktree a", X), ("rm --cached -f -q a", X), ("rm --quiet -- a", X),
            ("update-ref --no-deref -d refs/x", X), ("update-ref --no-deref refs/heads/main new old", X), ("branch -d topic", X), ("branch -dr origin/topic", X),
            ("merge --no-edit --no-ff topic", X), ("merge --abort", X), ("rebase --continue", X), ("cherry-pick --abort", X), ("revert --continue", X),
            ("apply --cached p", X), ("init --quiet", X), ("init --bare --quiet", X),
            ("hash-object -t commit -w --stdin", X), ("hash-object --stdin", R),
            // The reviewer's list of writes.
            ("symbolic-ref -d HEAD", X), ("reflog expire --all", X), ("reflog delete HEAD@{1}", X), ("reflog", R),
            ("config user.name x", X), ("config --unset user.name", X), ("config --list", R), ("remote remove origin", X),
            ("worktree add w", X), ("worktree remove w", X), ("worktree prune", X), ("worktree list", R),
            ("notes add -m n", X), ("notes list", R), ("gc", X), ("prune", X), ("repack -ad", X), ("pack-refs --all", X),
            ("lfs fetch", C), ("lfs push origin main", C), ("lfs pull", X), ("lfs prune", X), ("lfs ls-files", R),
            ("submodule update --init", X), ("submodule add url p", X), ("submodule status", R),
            ("merge-tree a b c", R), ("merge-tree --write-tree a b", X), ("branch -vv", R), ("branch --show-current", R), ("tag -l", R), ("tag v1", X),
        };

        [Test]
        public void EveryCommandIsClassifiedByWhatItDoes()
        {
            var wrong = Commands.Where(command => UnitGitWriteGate.Classify(command.line.Split(' ')) != command.kind)
                .Select(command => command.line + ": " + UnitGitWriteGate.Classify(command.line.Split(' ')) + ", expected " + command.kind).ToList();
            Assert.That(wrong, Is.Empty);
        }

        [Test]
        public void OnlyReadsCountAsReads()
        {
            foreach (var command in Commands)
                Assert.That(UnitGitWriteGate.IsRead(command.line.Split(' ')), Is.EqualTo(command.kind == R), command.line);
        }
    }
}
