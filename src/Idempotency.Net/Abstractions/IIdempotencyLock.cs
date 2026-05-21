using System;
using System.Collections.Generic;
using System.Text;

namespace Idempotency.Net.Abstractions
{
    public interface IIdempotencyLock
    {
        /// <summary>
        /// Пытается захватить блокировку для указанного ключа.
        /// </summary>
        /// <param name="key">Уникальный ключ операции.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>True, если блокировка успешно захвачена, иначе false.</returns>
        Task<bool> AcquireAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Освобождает блокировку для указанного ключа.
        /// </summary>
        Task ReleaseAsync(string key);
    }
}
