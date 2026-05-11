using Ambev.DeveloperEvaluation.Application.Users.CreateUser;
using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Unit.Domain;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class CreateUserHandlerTests
{
    private readonly IUserRepository _userRepository;
    private readonly IMapper _mapper;
    private readonly IPasswordHasher _passwordHasher;
    private readonly CreateUserHandler _handler;

    public CreateUserHandlerTests()
    {
        _userRepository = Substitute.For<IUserRepository>();
        _mapper = Substitute.For<IMapper>();
        _passwordHasher = Substitute.For<IPasswordHasher>();
        _handler = new CreateUserHandler(_userRepository, _mapper, _passwordHasher);
    }

    [Fact(DisplayName = "Valid command persists user, hashes password, returns Id")]
    public async Task Handle_ValidRequest_ReturnsSuccessResponse()
    {
        var command = CreateUserHandlerTestData.GenerateValidCommand();
        _passwordHasher.HashPassword(Arg.Any<string>()).Returns("hashedPassword");
        _userRepository.CreateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<User>());
        _mapper.Map<CreateUserResult>(Arg.Any<User>())
            .Returns(ci => new CreateUserResult { Id = ci.Arg<User>().Id });

        var createUserResult = await _handler.Handle(command, CancellationToken.None);

        createUserResult.Should().NotBeNull();
        createUserResult.Id.Should().NotBeEmpty();
        await _userRepository.Received(1).CreateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Password is hashed before save")]
    public async Task Handle_ValidRequest_HashesPassword()
    {
        var command = CreateUserHandlerTestData.GenerateValidCommand();
        var originalPassword = command.Password;
        const string hashedPassword = "h@shedPassw0rd";

        _passwordHasher.HashPassword(originalPassword).Returns(hashedPassword);
        _userRepository.CreateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<User>());
        _mapper.Map<CreateUserResult>(Arg.Any<User>())
            .Returns(ci => new CreateUserResult { Id = ci.Arg<User>().Id });

        await _handler.Handle(command, CancellationToken.None);

        _passwordHasher.Received(1).HashPassword(originalPassword);
        await _userRepository.Received(1).CreateAsync(
            Arg.Is<User>(u => u.Password == hashedPassword),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Handler hard-codes role=Customer and status=Active to defeat mass-assignment")]
    public async Task Handle_AlwaysAssignsCustomerRoleAndActiveStatus()
    {
        // Even though the command DTO has no Role/Status fields, the factory
        // (User.Create) hard-codes them. This test pins that contract.
        var command = CreateUserHandlerTestData.GenerateValidCommand();
        _passwordHasher.HashPassword(Arg.Any<string>()).Returns("hashedPassword");
        _userRepository.CreateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<User>());
        _mapper.Map<CreateUserResult>(Arg.Any<User>())
            .Returns(ci => new CreateUserResult { Id = ci.Arg<User>().Id });

        await _handler.Handle(command, CancellationToken.None);

        await _userRepository.Received(1).CreateAsync(
            Arg.Is<User>(u =>
                u.Role == UserRole.Customer &&
                u.Status == UserStatus.Active &&
                u.Username == command.Username &&
                u.Email == command.Email &&
                u.Phone == command.Phone),
            Arg.Any<CancellationToken>());
    }
}
