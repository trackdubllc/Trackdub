using System.Data.Common;

namespace Trackdub.Contracts;

public interface IScopedConnectionProvider : IDisposable
{
    DbConnection Connection { get; }
}
