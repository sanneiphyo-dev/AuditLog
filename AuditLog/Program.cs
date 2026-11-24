using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================
// 1. Dependency Injection Setup (MediatR & AuditLog Only)
// ============================================================================
var services = new ServiceCollection();

// Register MediatR
services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// Register Audit Log Repository & Services
services.AddSingleton<IAuditLogRepository, InMemoryAuditLogRepository>();
services.AddSingleton<TransferService>();

var provider = services.BuildServiceProvider();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("==================================================================");
Console.WriteLine("                 AUDIT LOG CONSOLE TEST APP                       ");
Console.WriteLine("==================================================================");
Console.ResetColor();

var transferService = provider.GetRequiredService<TransferService>();
var auditRepo = provider.GetRequiredService<IAuditLogRepository>();

// ============================================================================
// 2. Perform User Actions
// ============================================================================

// Action 1: userA transfers 100 Kyats to userB
Console.WriteLine("\n[Action 1] Executing Transfer...");
await transferService.TransferAsync(
    senderId: "userA",
    receiverId: "userB",
    amount: 100,
    performedBy: "userA"
);

// Action 2: userA changes daily limit
Console.WriteLine("[Action 2] Executing Limit Update...");
await transferService.UpdateLimitAsync(
    userId: "userA",
    oldLimit: 1_000,
    newLimit: 5_000,
    performedBy: "userA"
);

// ============================================================================
// 3. Display Audit Logs Saved in Database
// ============================================================================
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine("\n==================================================================");
Console.WriteLine("             SAVED AUDIT LOGS IN DATABASE                         ");
Console.WriteLine("==================================================================");
Console.ResetColor();

var logs = auditRepo.GetAll();
foreach (var log in logs)
{
    Console.WriteLine($"\n[Log ID: {log.Id}] Time: {log.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"• Actor:   {log.Actor}");
    Console.WriteLine($"• Action:  {log.Action}");
    Console.WriteLine($"• Target:  {log.Target}");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"{log.HumanReadableMessage}");
    Console.ResetColor();

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"• Data Changes:   {log.ChangesJson}");
    Console.ResetColor();
}

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("\n==================================================================");
Console.ResetColor();


#region AUDIT LOG MODEL & DATABASE REPOSITORY
public class AuditRecord
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string HumanReadableMessage { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = string.Empty;
}
#endregion

public interface IAuditLogRepository
{
    void Save(AuditRecord record);
    IReadOnlyList<AuditRecord> GetAll();
}

public class InMemoryAuditLogRepository : IAuditLogRepository
{
    private readonly List<AuditRecord> _records = new();
    private int _nextId = 1;

    public void Save(AuditRecord record)
    {
        record.Id = _nextId++;
        _records.Add(record);
    }

    public IReadOnlyList<AuditRecord> GetAll() => _records.AsReadOnly();
}


#region Audit Events
public record MoneyTransferredAuditEvent(
    string SenderId,
    string ReceiverId,
    decimal Amount,
    decimal SenderOldBalance,
    decimal SenderNewBalance,
    string PerformedBy
) : INotification;

public record LimitUpdatedAuditEvent(
    string UserId,
    decimal OldLimit,
    decimal NewLimit,
    string PerformedBy
) : INotification;
#endregion

#region BusinessLogic: TransferService  
public class TransferService
{
    private readonly IMediator _mediator;

    private static readonly Dictionary<string, decimal> Balances = new()
    {
        ["userA"] = 1_000m,
        ["userB"] = 500m
    };

    public TransferService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task TransferAsync(string senderId, string receiverId, decimal amount, string performedBy)
    {
        decimal oldBalance = Balances[senderId];
        Balances[senderId] -= amount;
        Balances[receiverId] += amount;
        decimal newBalance = Balances[senderId];

        // Trigger Audit Log via MediatR
        await _mediator.Publish(new MoneyTransferredAuditEvent(
            SenderId: senderId,
            ReceiverId: receiverId,
            Amount: amount,
            SenderOldBalance: oldBalance,
            SenderNewBalance: newBalance,
            PerformedBy: performedBy
        ));
    }

    public async Task UpdateLimitAsync(string userId, decimal oldLimit, decimal newLimit, string performedBy)
    {
        // Trigger Audit Log via MediatR
        await _mediator.Publish(new LimitUpdatedAuditEvent(
            UserId: userId,
            OldLimit: oldLimit,
            NewLimit: newLimit,
            PerformedBy: performedBy
        ));
    }
}
#endregion

#region TransferAuditHandler
public class TransferAuditHandler : INotificationHandler<MoneyTransferredAuditEvent>
{
    private readonly IAuditLogRepository _repository;

    public TransferAuditHandler(IAuditLogRepository repository)
    {
        _repository = repository;
    }

    public Task Handle(MoneyTransferredAuditEvent notification, CancellationToken cancellationToken)
    {
        var changes = new
        {
            Balance = new
            {
                Before = notification.SenderOldBalance,
                After = notification.SenderNewBalance
            }
        };

        var auditLog = new AuditRecord
        {
            Actor = notification.PerformedBy,
            Action = "TRANSFER",
            Target = notification.ReceiverId,
            // Human-Readable message
            HumanReadableMessage = $"data send from \"{notification.SenderId}\" to \"{notification.ReceiverId}\" about \"{notification.Amount:N0}kyats\"",
            ChangesJson = JsonSerializer.Serialize(changes)
        };

        _repository.Save(auditLog);
        return Task.CompletedTask;
    }
}
#endregion

#region LimitAuditHandler
public class LimitAuditHandler : INotificationHandler<LimitUpdatedAuditEvent>
{
    private readonly IAuditLogRepository _repository;

    public LimitAuditHandler(IAuditLogRepository repository)
    {
        _repository = repository;
    }

    public Task Handle(LimitUpdatedAuditEvent notification, CancellationToken cancellationToken)
    {
        var changes = new
        {
            DailyLimit = new
            {
                Before = notification.OldLimit,
                After = notification.NewLimit
            }
        };

        var auditLog = new AuditRecord
        {
            Actor = notification.PerformedBy,
            Action = "UPDATE_LIMIT",
            Target = notification.UserId,
            // Human-Readable message
            HumanReadableMessage = $"\"{notification.PerformedBy}\" changed daily limit from \"{notification.OldLimit:N0}kyats\" to \"{notification.NewLimit:N0}kyats\"",
            ChangesJson = JsonSerializer.Serialize(changes)
        };

        _repository.Save(auditLog);
        return Task.CompletedTask;
    }
}
#endregion