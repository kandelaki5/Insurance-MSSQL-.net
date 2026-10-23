using System.Data;
using Insurance.Application.Data;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>The error a test makes one statement fail with: the caller must get this very object back.</summary>
public sealed class InjectedFault(string name) : Exception(name);

/// <summary>
/// The tests' command interceptor (DbOptions.Interceptor): every command a Db with it sends is logged, and armed rules fire on
/// the first command they match — a statement is matched by a stable fragment of its SQL (the table it reads) and, where
/// it matters, by the transaction it runs in, so the quick permission check (Db.SnapshotAsync, Snapshot) and the binding one
/// (Db.AsUserAsync, ReadCommitted) are told apart without counting calls. A rule fires once and is then spent.
/// </summary>
public sealed class Faults : ICommandInterceptor
{
    private sealed class Rule(Func<CommandEvent, bool> when, Action<CommandEvent> act)
    {
        public Func<CommandEvent, bool> When { get; } = when;
        public Action<CommandEvent> Act { get; } = act;
        public bool Spent { get; set; }
    }

    private readonly Lock gate = new();
    private readonly List<Rule> rules = [];
    private readonly List<CommandEvent> log = [];

    /// <summary>Every command seen so far, in order.</summary>
    public IReadOnlyList<CommandEvent> Log { get { lock (gate) return [.. log]; } }

    /// <summary>The first command matching <paramref name="when"/> fails with <paramref name="error"/> (it is never sent).</summary>
    public Faults FailOnce(Func<CommandEvent, bool> when, Exception error) => Add(when, _ => throw error);

    /// <summary>Before the first command matching <paramref name="when"/>, <paramref name="action"/> runs (on another connection); the command then runs.</summary>
    public Faults BeforeOnce(Func<CommandEvent, bool> when, Action action) => Add(when, _ => action());

    private Faults Add(Func<CommandEvent, bool> when, Action<CommandEvent> act)
    {
        lock (gate) rules.Add(new Rule(when, act));
        return this;
    }

    /// <summary>Whether every armed rule has fired.</summary>
    public bool AllFired { get { lock (gate) return rules.All(r => r.Spent); } }

    /// <summary>Forgets the log and the rules.</summary>
    public void Reset()
    {
        lock (gate) { rules.Clear(); log.Clear(); }
    }

    public void ClearLog()
    {
        lock (gate) log.Clear();
    }

    public void BeforeExecute(CommandEvent command)
    {
        Rule? fire;
        lock (gate)
        {
            log.Add(command);
            fire = rules.FirstOrDefault(r => !r.Spent && r.When(command));
            if (fire is not null) fire.Spent = true;
        }
        fire?.Act(command);
    }

    // ---- statements, by a stable fragment of their SQL (src/Insurance.Application/Services/PermissionStore.cs)

    public const string Overrides = "from core.user_permission where user_id = @userId";
    public const string Memberships = "from core.user_group_member where user_id = @userId";
    public const string Grants = "from core.user_group_permission where group_id in";

    /// <summary>The three reads of a permission check, by name (legacy SQL.overrides, SQL.memberships, SQL.grants).</summary>
    public static readonly IReadOnlyList<(string Name, string Fragment)> PermissionReads =
        [("own decisions", Overrides), ("memberships", Memberships), ("group grants", Grants)];

    public static bool Has(CommandEvent c, string fragment) => c.Sql.Contains(fragment, StringComparison.Ordinal);

    /// <summary>The statement in the quick check (a snapshot read before the transaction) — or, for a page, its permission read.</summary>
    public static Func<CommandEvent, bool> Quick(string fragment) => c => Has(c, fragment) && c.Isolation == IsolationLevel.Snapshot;

    /// <summary>The statement in the writing transaction (Db.AsUserAsync).</summary>
    public static Func<CommandEvent, bool> Binding(string fragment) => c => Has(c, fragment) && c.Isolation == IsolationLevel.ReadCommitted;

    /// <summary>The statement wherever it runs.</summary>
    public static Func<CommandEvent, bool> Any(string fragment) => c => Has(c, fragment);

    /// <summary>The statements of the last transaction of a user (Db.AsUserAsync), from its first statement on.</summary>
    public List<string> LastTransaction()
    {
        var log = Log.ToList();
        var start = log.FindLastIndex(c => c.Sql == "set xact_abort on");
        Assert.True(start >= 0, "no transaction was begun");
        return log.Skip(start).TakeWhile(c => c.Isolation == IsolationLevel.ReadCommitted).Select(c => c.Sql).ToList();
    }

    /// <summary>Whether any transaction was begun since the log was cleared.</summary>
    public bool BegunATransaction => Log.Any(c => c.Sql == "set xact_abort on");

    /// <summary>
    /// The binding check refused: the last transaction read the user's permissions (the group grants only when the user is in
    /// a group) and nothing else — nothing of the record it would have changed, no lock, no write — and then ended.
    /// </summary>
    public void AssertOnlyThePermissionWasRead(bool grants = true)
    {
        var tx = LastTransaction();
        Assert.Equal(grants ? 6 : 5, tx.Count);
        Assert.Equal("set xact_abort on", tx[0]);
        Assert.Contains("N'core.tx_at'", tx[1], StringComparison.Ordinal);
        Assert.StartsWith("exec sp_set_session_context N'core.user_id'", tx[2], StringComparison.Ordinal);
        Assert.Contains(Overrides, tx[3], StringComparison.Ordinal);
        Assert.Contains(Memberships, tx[4], StringComparison.Ordinal);
        if (grants) Assert.Contains(Grants, tx[5], StringComparison.Ordinal);
    }

    /// <summary>The quick check and the binding one, each with the three reads (the legacy tests' [mode, label] × [name, text]).</summary>
    public static TheoryData<string, string> QuickAndBinding()
    {
        var data = new TheoryData<string, string>();
        foreach (var label in new[] { "quick", "binding" })
            foreach (var (name, _) in PermissionReads) data.Add(label, name);
        return data;
    }

    /// <summary>One read of the quick ("quick") or the binding ("binding") check, by its name.</summary>
    public static Func<CommandEvent, bool> Read(string label, string name)
    {
        var fragment = PermissionReads.Single(r => r.Name == name).Fragment;
        return label == "quick" ? Quick(fragment) : Binding(fragment);
    }
}
