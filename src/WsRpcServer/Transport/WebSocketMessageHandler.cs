using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using WsRpcServer.Core;
using WsRpcServer.Logging;
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

    /// <summary>
    /// Лічильник послідовних помилок розбору JSON. Скидається після успішної десеріалізації;
    /// при досягненні <see cref="JsonRpcServerConfig.MaxConsecutiveParseFailures"/> з'єднання закривається.
    /// </summary>
    private int _consecutiveParseFailures;

    /// <summary>
    /// Поріг послідовних помилок розбору, після якого з'єднання примусово закривається (H2 anti-DoS).
    /// </summary>
    private readonly int _maxConsecutiveParseFailures;

    public WebSocketMessageHandler(
        IJsonRpcSession session,
        IJsonRpcMessageFormatter formatter,
        ILogger<WebSocketMessageHandler> logger,
        JsonRpcServerConfig config)
        : base(formatter)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxConsecutiveParseFailures = config?.MaxConsecutiveParseFailures ?? 10;

        // Налаштовуємо канал з пороговим значенням
        var receivePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: config?.PipeThresholdBytes ?? 1024 * 1024)); // За замовчуванням 1МБ

        _reader = receivePipe.Reader;
        _writer = receivePipe.Writer;

        WebSocketMessageHandlerLog.HandlerCreated(_logger, session.Id, config?.PipeThresholdBytes ?? 1024 * 1024);
    }

    // Базові властивості, необхідні для StreamJsonRpc.
    // Після утилізації повертаємо false, щоб StreamJsonRpc припинив викликати Read/Write
    // у вже звільнений обробник (M9).
    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;

    /// <summary>
    /// Обробляє отримані дані WebSocket, записуючи їх у канал.
    /// Підтримує фрагментацію повідомлень шляхом накопичення даних до отримання повного повідомлення.
    /// </summary>
    public ValueTask<FlushResult> ProcessReceivedDataAsync(ReadOnlyMemory<byte> buffer)
    {
        try
        {
            WebSocketMessageHandlerLog.ProcessingReceivedData(_logger, buffer.Length, _session.Id);

            // Записуємо вхідні дані в канал
            _writer.Write(buffer.Span);

            // Передаємо дані читачу
            return _writer.FlushAsync();
        }
        catch (Exception ex)
        {
            WebSocketMessageHandlerLog.ProcessDataError(_logger, ex, _session.Id);
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
                WebSocketMessageHandlerLog.DeserializationComplete(_logger, _session.Id);
                _bufferedMessage.DeserializationComplete(message);
                _bufferedMessage = null;
            }
        }
        catch (Exception ex)
        {
            WebSocketMessageHandlerLog.DeserializationCompleteError(_logger, ex, _session.Id);
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
                WebSocketMessageHandlerLog.ReadCanceledOrCompleted(_logger, _session.Id);
                return null;
            }

            JsonRpcMessage? message = null;
            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;
            bool parseLimitExceeded = false;

            try
            {
                WebSocketMessageHandlerLog.AttemptingDeserialize(_logger, _session.Id);
                message = Formatter.Deserialize(buffer);
                _bufferedMessage = message as IJsonRpcMessageBufferManager;

                // Якщо повідомлення успішно десеріалізовано, все оброблено
                consumed = buffer.End;

                // Успішний розбір — скидаємо лічильник послідовних помилок (H2).
                _consecutiveParseFailures = 0;

                WebSocketMessageHandlerLog.DeserializeOk(_logger, message.GetType().Name, _session.Id);
            }
            catch (JsonException jsonEx)
            {
                _consecutiveParseFailures++;

                // Логуємо на Warning (не Error) — це очікуваний клас помилок від клієнта, а не збій сервера.
                WebSocketMessageHandlerLog.ParseError(_logger, jsonEx, _session.Id, _consecutiveParseFailures,
                    _maxConsecutiveParseFailures, jsonEx.BytePositionInLine);

                if (_consecutiveParseFailures >= _maxConsecutiveParseFailures)
                {
                    // Захист від CPU-burn DoS: припиняємо нескінченний цикл відновлення (H2).
                    parseLimitExceeded = true;
                    consumed = buffer.End;
                }
                else if (jsonEx.BytePositionInLine > 0)
                {
                    int errorPos = Math.Max(0, (int)jsonEx.BytePositionInLine);
                    // Шукаємо наступний роздільник JSON-об'єкту, щоб не втратити дані після помилки.
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
                WebSocketMessageHandlerLog.UnexpectedDeserializeError(_logger, ex, _session.Id);
                consumed = buffer.End;
                throw;
            }
            finally
            {
                _reader.AdvanceTo(consumed, examined);
            }

            if (parseLimitExceeded)
            {
                WebSocketMessageHandlerLog.ParseLimitExceeded(_logger, _maxConsecutiveParseFailures, _session.Id);

                _session.Close(WebSocketCloseStatus.ProtocolError, "Too many consecutive malformed JSON messages");
                await _reader.CompleteAsync().ConfigureAwait(false);
                return null;
            }

            return message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WebSocketMessageHandlerLog.ReadCanceled(_logger, _session.Id);
            return null;
        }
        catch (Exception ex)
        {
            WebSocketMessageHandlerLog.ReadError(_logger, ex, _session.Id);
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
            WebSocketMessageHandlerLog.WriteAfterDispose(_logger, _session.Id);
            throw new ObjectDisposedException(nameof(WebSocketMessageHandler));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebSocketMessageHandlerLog.SerializingMessage(_logger, _session.Id);

            // Використовуємо ArrayBufferWriter для ефективної серіалізації
            var writer = new ArrayBufferWriter<byte>(1024);

            Formatter.Serialize(writer, content);

            if (Formatter is IJsonRpcFormatterTracingCallbacks tracer)
            {
                var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
                tracer.OnSerializationComplete(content, sequence);
            }

            // Використовуємо NetCoreServer для надсилання бінарних даних
            await _session.SendBinaryDataAsync(writer.WrittenMemory).ConfigureAwait(false);

            WebSocketMessageHandlerLog.MessageSent(_logger, writer.WrittenCount, _session.Id);
        }
        catch (Exception ex)
        {
            WebSocketMessageHandlerLog.SendMessageError(_logger, ex, _session.Id);
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
                WebSocketMessageHandlerLog.Disposing(_logger, _session.Id);
                _writeLock.Dispose();
                _writer.Complete();
                _reader.Complete();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}