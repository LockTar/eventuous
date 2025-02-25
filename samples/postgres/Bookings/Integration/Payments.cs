using Bookings.Domain.Bookings;
using Eventuous;
using static Bookings.Application.BookingCommands;
using static Bookings.Integration.IntegrationEvents;
using EventHandler = Eventuous.Subscriptions.EventHandler;

namespace Bookings.Integration;

public class PaymentsIntegrationHandler : EventHandler {
    public const string Stream = "PaymentsIntegration";

    readonly ICommandService<BookingState> _applicationService;

    public PaymentsIntegrationHandler(ICommandService<BookingState> applicationService) {
        _applicationService = applicationService;
        On<BookingPaymentRecorded>(async ctx => await HandlePayment(ctx.Message, ctx.CancellationToken));
    }

    Task HandlePayment(BookingPaymentRecorded evt, CancellationToken cancellationToken)
        => _applicationService.Handle(
            new RecordPayment(
                evt.BookingId,
                evt.Amount,
                evt.Currency,
                evt.PaymentId,
                ""
            ),
            cancellationToken
        );
}

static class IntegrationEvents {
    [EventType("BookingPaymentRecorded")]
    public record BookingPaymentRecorded(string PaymentId, string BookingId, float Amount, string Currency);
}