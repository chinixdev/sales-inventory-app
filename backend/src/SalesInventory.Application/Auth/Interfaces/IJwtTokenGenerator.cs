using SalesInventory.Application.Auth.DTOs;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Auth.Interfaces;

public interface IJwtTokenGenerator
{
    LoginResponseDto Generate(User user);
}
