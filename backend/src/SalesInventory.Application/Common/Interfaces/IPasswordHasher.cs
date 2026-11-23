using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(User user, string password);
}
