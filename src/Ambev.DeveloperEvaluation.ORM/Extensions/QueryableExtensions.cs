using System.Linq.Expressions;
using System.Reflection;

namespace Ambev.DeveloperEvaluation.ORM.Extensions;

/// <summary>
/// IQueryable helpers used by repositories.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Applies an ordering expression like <c>"saleDate desc, totalAmount asc"</c>
    /// to an IQueryable, restricted to a caller-supplied whitelist of property
    /// names. Anything outside the whitelist is rejected with
    /// <see cref="ArgumentException"/>, so a malicious client cannot order by
    /// arbitrary entity columns (e.g. password hashes) or trigger full table
    /// scans on un-indexed fields. An empty/null expression leaves the source
    /// untouched.
    /// </summary>
    /// <param name="source">The query to order.</param>
    /// <param name="order">Comma-separated ordering expression.</param>
    /// <param name="allowedProperties">
    /// Whitelist of property names matched case-insensitively. Use the entity
    /// property names exactly as they exist on the type.
    /// </param>
    public static IQueryable<T> OrderByDynamic<T>(
        this IQueryable<T> source,
        string? order,
        IReadOnlyCollection<string> allowedProperties)
    {
        if (string.IsNullOrWhiteSpace(order))
            return source;

        ArgumentNullException.ThrowIfNull(allowedProperties);

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        IQueryable<T> current = source;
        var first = true;

        foreach (var raw in order.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var propertyName = parts[0];
            var descending = parts.Length > 1 &&
                parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

            if (!allowedProperties.Any(a => a.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"'{propertyName}' is not a valid sort field. Allowed fields: {string.Join(", ", allowedProperties)}.",
                    nameof(order));

            var property = properties.FirstOrDefault(p =>
                p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property is null) continue;

            var parameter = Expression.Parameter(typeof(T), "x");
            var access = Expression.MakeMemberAccess(parameter, property);
            var lambda = Expression.Lambda(access, parameter);

            var methodName = first
                ? (descending ? "OrderByDescending" : "OrderBy")
                : (descending ? "ThenByDescending" : "ThenBy");

            var call = Expression.Call(
                typeof(Queryable),
                methodName,
                new[] { typeof(T), property.PropertyType },
                current.Expression,
                Expression.Quote(lambda));

            current = current.Provider.CreateQuery<T>(call);
            first = false;
        }

        return current;
    }
}
