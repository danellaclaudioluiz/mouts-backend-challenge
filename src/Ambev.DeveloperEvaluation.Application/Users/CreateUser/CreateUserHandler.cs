using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Users.CreateUser;

/// <summary>
/// Self-service signup handler. Routes through <see cref="User.Create"/>
/// which hard-codes role=Customer + status=Active so a smuggled
/// <c>role: "Admin"</c> on the request body cannot escalate privileges.
/// </summary>
public class CreateUserHandler : IRequestHandler<CreateUserCommand, CreateUserResult>
{
    private readonly IUserRepository _userRepository;
    private readonly IMapper _mapper;
    private readonly IPasswordHasher _passwordHasher;

    public CreateUserHandler(IUserRepository userRepository, IMapper mapper, IPasswordHasher passwordHasher)
    {
        _userRepository = userRepository;
        _mapper = mapper;
        _passwordHasher = passwordHasher;
    }

    public async Task<CreateUserResult> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        // Cheap pre-check; the unique index on Users.Email is the source of
        // truth and a concurrent insert is mapped to 409 by the middleware.
        if (await _userRepository.EmailExistsAsync(command.Email, cancellationToken))
            throw new ConflictException($"User with email {command.Email} already exists");

        // Build via the rich factory — no AutoMapper hop, no chance of a
        // future profile leaking Role/Status into the entity.
        var user = User.Create(
            username: command.Username,
            passwordHash: _passwordHasher.HashPassword(command.Password),
            email: command.Email,
            phone: command.Phone);

        var createdUser = await _userRepository.CreateAsync(user, cancellationToken);
        return _mapper.Map<CreateUserResult>(createdUser);
    }
}
