using MediatR;

namespace AgronomoPlus.Application.Shared.Abstractions.Messaging;

public interface ICommandHandler<TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : IRequest<TResponse>, ICommand<TResponse>
{
}
