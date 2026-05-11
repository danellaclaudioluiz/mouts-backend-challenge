using AutoMapper;
using Ambev.DeveloperEvaluation.Application.Users.GetUser;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Users.GetUser;

public class GetUserProfile : Profile
{
    public GetUserProfile()
    {
        CreateMap<Guid, GetUserCommand>()
            .ConstructUsing(id => new GetUserCommand(id));
        // Was missing — GetUser controller calls _mapper.Map<GetUserResponse>(result)
        // and AutoMapper would throw AutoMapperMappingException without it,
        // surfacing as a 500 for any GET /api/v1/users/{id} caller.
        CreateMap<GetUserResult, GetUserResponse>();
    }
}
