using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using WsRpcServer.Core;
using WsRpcServer.Sessions;

namespace WsRpcServer.Transport;

/// <summary>
/// Обробник повідомлень WebSocket, що інтегрує NetCoreServer із StreamJsonRpc.
/// Забезпечує ефективну обробку повідомлень з буферизацією та обробкою помилок.
/// </summary>
public sealed class WebSocketMessageHandler : MessageHandlerBase, IJsonRpcMessageBufferManager
{
    private readonly ILogger _logger;
    private readonly IJsonRpcSession _session;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private IJsonRpcMessageBufferManager? _bufferedMessage;
    private bool _disposed;

    public WebSocketMessageHandler(
        IJsonRpcSession session,
        IJsonRpcMessageFormatter formatter,
        ILogger<WebSocketMessageHandler> logger,
        JsonRpcServerConfig config)
        : base(formatter)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Налаштовуємо канал з пороговим значенням
        var receivePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: config?.PipeThresholdBytes ?? 1024 * 1024)); // За замовчуванням 1МБ

        _reader = receivePipe.Reader;
        _writer = receivePipe.Writer;

        _logger.LogDebug("Створено WebSocketMessageHandler для сесії {SessionId} з порогом {Threshold} байт",
            session.Id, config?.PipeThresholdBytes ?? 1024 * 1024);
    }

    // Базові властивості, необхідні для StreamJsonRpc
    public override bool CanRead => true;
    public override bool CanWrite => true;

    /// <summary>
    /// Обробляє отримані дані WebSocket, записуючи їх у канал.
    /// Підтримує фрагментацію повідомлень шляхом накопичення даних до отримання повного повідомлення.
    /// </summary>
    public ValueTask<FlushResult> ProcessReceivedDataAsync(ReadOnlyMemory<byte> buffer)
    {
        try
        {
            _logger.LogDebug("Обробка отриманих даних розміром {Size} байт для сесії {SessionId}",
                buffer.Length, _session.Id);

            // Записуємо вхідні дані в канал
            _writer.Write(buffer.Span);

            // Передаємо дані читачу
            return _writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка обробки даних WebSocket для сесії {SessionId}", _session.Id);
            throw;
        }
    }

    /// <summary>
    /// Реалізує IJsonRpcMessageBufferManager.DeserializationComplete для належного управління життєвим циклом буфера повідомлень.
    /// </summary>
    void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage? message)
    {
        if (_disposed)
            return;

        try
        {
            if (message is not null && _bufferedMessage == message)
            {
                _logger.LogDebug("Завершено десеріалізацію повідомлення для сесії {SessionId}", _session.Id);
                _bufferedMessage.DeserializationComplete(message);
                _bufferedMessage = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка завершення десеріалізації повідомлення для сесії {SessionId}", _session.Id);
        }
    }

    /// <summary>
    /// Читає повідомлення JSON-RPC з каналу, підтримуючи фрагментовані повідомлення WebSocket.
    /// </summary>
    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var readResult = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = readResult.Buffer;

            if (readResult.IsCanceled || (buffer.IsEmpty && readResult.IsCompleted))
            {
                _logger.LogDebug("Читання скасовано або завершено для сесії {SessionId}", _session.Id);
                return null;
            }

            JsonRpcMessage? message = null;
            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                _logger.LogDebug("Спроба десеріалізації JSON-RPC повідомлення для сесії {SessionId}", _session.Id);
                message = Formatter.Deserialize(buffer);
                _bufferedMessage = message as IJsonRpcMessageBufferManager;

                // Якщо повідомлення успішно десеріалізовано, все оброблено
                consumed = buffer.End;

                _logger.LogDebug("Успішно десеріалізовано повідомлення типу {MessageType} для сесії {SessionId}",
                    message.GetType().Name, _session.Id);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx,
                    "Помилка десеріалізації JSON-RPC повідомлення для сесії {SessionId}. Позиція: {Position}",
                    _session.Id, jsonEx.BytePositionInLine);

                // Розумніша стратегія відновлення - спробуємо просунутися до позиції помилки,
                // щоб не втратити дані після неї
                if (jsonEx.BytePositionInLine > 0)
                {
                    int errorPos = Math.Max(0, (int)jsonEx.BytePositionInLine);
                    // Шукаємо наступний роздільник JSON-об'єкту
                    consumed = FindNextJsonDelimiter(buffer, errorPos);
                }
                else
                {
                    // Якщо не вдається відновитись, пропускаємо весь буфер
                    consumed = buffer.End;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Неочікувана помилка під час десеріалізації повідомлення для сесії {SessionId}",
                    _session.Id);
                consumed = buffer.End;
                throw;
            }
            finally
            {
                _reader.AdvanceTo(consumed, examined);
            }

            return message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Операцію читання скасовано для сесії {SessionId}", _session.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неочікувана помилка читання даних WebSocket для сесії {SessionId}", _session.Id);
            throw;
        }
    }

    /// <summary>
    /// Знаходить наступний роздільник JSON-об'єкту в буфері
    /// </summary>
    private SequencePosition FindNextJsonDelimiter(ReadOnlySequence<byte> buffer, int startPosition)
    {
        // Копіюємо частину буфера для аналізу
        int segmentSize = Math.Min(1024, (int)buffer.Length - startPosition);
        if (segmentSize <= 0) return buffer.End;

        byte[] tempBuffer = _bufferPool.Rent(segmentSize);
        try
        {
            var segment = buffer.Slice(startPosition, segmentSize);
            segment.CopyTo(tempBuffer);

            // Шукаємо символи, які можуть бути роздільниками JSON
            for (int i = 0; i < segmentSize; i++)
            {
                char c = (char)tempBuffer[i];
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == ',')
                {
                    // Знайдено потенційний роздільник, повертаємо позицію після нього
                    return buffer.GetPosition(startPosition + i + 1);
                }
            }
        }
        finally
        {
            _bufferPool.Return(tempBuffer);
        }

        // Роздільник не знайдено, пропускаємо весь буфер
        return buffer.End;
    }

    /// <summary>
    /// Записує повідомлення JSON-RPC у WebSocket з мінімальними виділеннями пам'яті.
    /// </summary>
    protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            _logger.LogWarning("Спроба запису в утилізований WebSocketMessageHandler для сесії {SessionId}",
                _session.Id);
            throw new ObjectDisposedException(nameof(WebSocketMessageHandler));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Серіалізація та надсилання JSON-RPC повідомлення для сесії {SessionId}", _session.Id);

            // Використовуємо ArrayBufferWriter для ефективної серіалізації
            var writer = new ArrayBufferWriter<byte>(1024);

            Formatter.Serialize(writer, content);

            if (Formatter is IJsonRpcFormatterTracingCallbacks tracer)
            {
                var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
                tracer.OnSerializationComplete(content, sequence);
            }

            // Використовуємо NetCoreServer для надсилання бінарних даних
            await _session.SendBinaryDataAsync(writer.WrittenMemory);

            _logger.LogDebug("Надіслано JSON-RPC повідомлення розміром {Size} байт для сесії {SessionId}",
                writer.WrittenCount, _session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка надсилання JSON-RPC повідомлення для сесії {SessionId}", _session.Id);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken) => default;

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _logger.LogDebug("Утилізація WebSocketMessageHandler для сесії {SessionId}", _session.Id);
                _writeLock.Dispose();
                _writer.Complete();
                _reader.Complete();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}