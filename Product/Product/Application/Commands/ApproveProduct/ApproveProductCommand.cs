using Application.Common;

namespace Application.Commands.ApproveProduct;

public sealed record ApproveProductCommand(Guid ProductId) : ICommand<Result>;
