using Candela.Modules.Sales.Hardware.Dtos;

namespace Candela.Modules.Sales.Hardware;

/// <summary>
/// Opens the cash drawer. There is no repository here — nothing touches the database;
/// the work is a socket write or a spooler call.
///
/// This slice gets a service rather than living in the controller because it holds real
/// decisions: which of the two paths to take, the SSRF check before reaching out, the
/// connect and write timeouts, and which failures are the caller's fault.
/// </summary>
public interface IDrawerService
{
    /// <summary>
    /// Kicks the drawer and describes what it talked to, for the response.
    ///
    /// Throws ValidationException when the target is missing or rejected by PrinterGuard,
    /// and PrinterUnreachableException when the printer does not answer.
    /// </summary>
    Task<DrawerResponse> OpenAsync(DrawerRequest req, string posCode, CancellationToken ct);
}
