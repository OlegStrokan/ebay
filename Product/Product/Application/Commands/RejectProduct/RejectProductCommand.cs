using Application.Common;

namespace Application.Commands.RejectProduct;

public sealed record RejectProductCommand(Guid ProductId, string Reason) : ICommand<Result>;
