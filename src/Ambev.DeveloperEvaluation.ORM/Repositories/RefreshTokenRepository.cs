using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Ambev.DeveloperEvaluation.ORM.Repositories;

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly DefaultContext _context;

    public RefreshTokenRepository(DefaultContext context)
    {
        _context = context;
    }

    public async Task<RefreshToken> CreateAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        await _context.Set<RefreshToken>().AddAsync(token, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return token;
    }

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        return _context.Set<RefreshToken>()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
    }

    public async Task UpdateAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        _context.Set<RefreshToken>().Update(token);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
