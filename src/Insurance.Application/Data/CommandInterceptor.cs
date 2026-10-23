using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;

namespace Insurance.Application.Data;

/// <summary>A command about to be sent on a connection opened by a <see cref="Db"/> that has an interceptor.</summary>
/// <param name="Sql">The command's text, exactly as written in the code (a procedure's name for a stored procedure).</param>
/// <param name="Isolation">The isolation level of the command's transaction; null when it runs outside a transaction
/// (Db.ReadAsync / Db.ReadTxAsync). Db.SnapshotAsync reads run in Snapshot, Db.AsUserAsync / InTransactionAsync in ReadCommitted.</param>
/// <param name="Parameters">The command's parameters by name (without the @), as they are sent.</param>
public sealed record CommandEvent(string Sql, IsolationLevel? Isolation, IReadOnlyDictionary<string, object?> Parameters)
{
    public bool InTransaction => Isolation is not null;

    /// <summary>A parameter's value, or null when the command has none of that name.</summary>
    public object? Parameter(string name) => Parameters.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// A test seam (DbOptions.Interceptor): called before every command sent on a connection that a <see cref="Db"/> with this
/// interceptor opened — by Tx, Db.ReadAsync, Dapper on tx.Connection, the transaction's own set-up statements, all of them.
/// It may throw, and the command is then never sent: the exception comes out of the call that executes it, exactly as a
/// failed read or write would (a transaction then rolls back). It may also do something first — for example revoke a grant
/// through another connection — and return, and the command then runs as usual. It is synchronous and runs on the caller's
/// thread. Production never sets it (Program.cs, ServiceRegistration); unset, nothing is subscribed and a connection costs
/// one null check.
/// </summary>
public interface ICommandInterceptor
{
    void BeforeExecute(CommandEvent command);
}

/// <summary>
/// How the interceptor is reached: SqlClient announces every command on its diagnostic listener ("SqlClientDiagnosticListener",
/// event Microsoft.Data.SqlClient.WriteCommandBefore) synchronously before it is sent, and an exception thrown there leaves
/// the execute call. Only connections registered by a Db with an interceptor are looked at; the subscription is made the
/// first time one is registered, so an application that never sets one never subscribes.
/// </summary>
internal static class CommandInterception
{
    private const string ListenerName = "SqlClientDiagnosticListener";
    private const string BeforeEvent = "Microsoft.Data.SqlClient.WriteCommandBefore";

    private static readonly ConditionalWeakTable<SqlConnection, ICommandInterceptor> Connections = new();
    private static readonly Lazy<IDisposable> Subscription = new(() => DiagnosticListener.AllListeners.Subscribe(new Listeners()), LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Attach(SqlConnection connection, ICommandInterceptor interceptor)
    {
        _ = Subscription.Value;
        Connections.AddOrUpdate(connection, interceptor);
    }

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        private readonly ConcurrentBag<IDisposable> subscriptions = [];

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == ListenerName) subscriptions.Add(listener.Subscribe(new Commands(), name => name == BeforeEvent));
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class Commands : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != BeforeEvent || value.Value is not SqlClientCommandBefore { Command: SqlCommand command }) return;
            if (command.Connection is not { } connection || !Connections.TryGetValue(connection, out var interceptor)) return;
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (SqlParameter p in command.Parameters)
                parameters[p.ParameterName.TrimStart('@')] = p.Value is DBNull ? null : p.Value;
            interceptor.BeforeExecute(new CommandEvent(command.CommandText, command.Transaction?.IsolationLevel, parameters));
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
