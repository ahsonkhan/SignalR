// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.JsonLab;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Microsoft.AspNetCore.SignalR.Protocol
{
    /// <summary>
    /// Implements the SignalR Hub Protocol using JSON.
    /// </summary>
    public class JsonHubProtocol : IHubProtocol
    {
        private const string ResultPropertyName = "result";
        private const string ItemPropertyName = "item";
        private const string InvocationIdPropertyName = "invocationId";
        private const string StreamIdPropertyName = "streamId";
        private const string TypePropertyName = "type";
        private const string ErrorPropertyName = "error";
        private const string TargetPropertyName = "target";
        private const string ArgumentsPropertyName = "arguments";
        private const string HeadersPropertyName = "headers";

        private static readonly byte[] ResultPropertyNameUtf8 = Encoding.UTF8.GetBytes("result");
        private static readonly byte[] ItemPropertyNameUtf8 = Encoding.UTF8.GetBytes("item");
        private static readonly byte[] InvocationIdPropertyNameUtf8 = Encoding.UTF8.GetBytes("invocationId");
        private static readonly byte[] StreamIdPropertyNameUtf8 = Encoding.UTF8.GetBytes("streamId");
        private static readonly byte[] TypePropertyNameUtf8 = Encoding.UTF8.GetBytes("type");
        private static readonly byte[] ErrorPropertyNameUtf8 = Encoding.UTF8.GetBytes("error");
        private static readonly byte[] TargetPropertyNameUtf8 = Encoding.UTF8.GetBytes("target");
        private static readonly byte[] ArgumentsPropertyNameUtf8 = Encoding.UTF8.GetBytes("arguments");
        private static readonly byte[] HeadersPropertyNameUtf8 = Encoding.UTF8.GetBytes("headers");

        private static readonly string ProtocolName = "json";
        private static readonly int ProtocolVersion = 1;
        private static readonly int ProtocolMinorVersion = 0;

        /// <summary>
        /// Gets the serializer used to serialize invocation arguments and return values.
        /// </summary>
        public JsonSerializer PayloadSerializer { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonHubProtocol"/> class.
        /// </summary>
        public JsonHubProtocol() : this(Options.Create(new JsonHubProtocolOptions()))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonHubProtocol"/> class.
        /// </summary>
        /// <param name="options">The options used to initialize the protocol.</param>
        public JsonHubProtocol(IOptions<JsonHubProtocolOptions> options)
        {
            PayloadSerializer = JsonSerializer.Create(options.Value.PayloadSerializerSettings);
        }

        /// <inheritdoc />
        public string Name => ProtocolName;

        /// <inheritdoc />
        public int Version => ProtocolVersion;

        /// <inheritdoc />        
        public int MinorVersion => ProtocolMinorVersion;

        /// <inheritdoc />
        public TransferFormat TransferFormat => TransferFormat.Text;

        /// <inheritdoc />
        public bool IsVersionSupported(int version)
        {
            return version == Version;
        }

        /// <inheritdoc />
        public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, out HubMessage message)
        {
            if (!TextMessageParser.TryParseMessage(ref input, out var payload))
            {
                message = null;
                return false;
            }

            message = ParseMessage(new Utf8JsonReader(payload), binder);

            return message != null;
        }

        /// <inheritdoc />
        public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
        {
            WriteMessageCore(message, output);
            TextMessageFormatter.WriteRecordSeparator(output);
        }

        /// <inheritdoc />
        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
        {
            return HubProtocolExtensions.GetMessageBytes(this, message);
        }

        private HubMessage ParseMessage(Utf8JsonReader reader, IInvocationBinder binder)
        {
            try
            {
                // We parse using the JsonTextReader directly but this has a problem. Some of our properties are dependent on other properties
                // and since reading the json might be unordered, we need to store the parsed content as JToken to re-parse when true types are known.
                // if we're lucky and the state we need to directly parse is available, then we'll use it.

                int? type = null;
                string invocationId = null;
                string streamId = null;
                string target = null;
                string error = null;
                var hasItem = false;
                object item = null;
                JToken itemToken = null;
                var hasResult = false;
                object result = null;
                JToken resultToken = null;
                var hasArguments = false;
                object[] arguments = null;
                JArray argumentsToken = null;
                ExceptionDispatchInfo argumentBindingException = null;
                Dictionary<string, string> headers = null;
                var completed = false;

                JsonUtils.CheckRead(reader);

                // We're always parsing a JSON object
                JsonUtils.EnsureObjectStart(reader);

                do
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            ReadOnlySpan<byte> memberName = reader.Value;

                            if (memberName.SequenceEqual(TypePropertyNameUtf8))
                            {
                                var messageType = JsonUtils.ReadAsInt32(reader, TypePropertyName);

                                if (messageType == null)
                                {
                                    throw new InvalidDataException($"Missing required property '{TypePropertyName}'.");
                                }

                                type = messageType.Value;
                            }
                            else if (memberName.SequenceEqual(InvocationIdPropertyNameUtf8))
                            {
                                invocationId = JsonUtils.ReadAsString(reader, InvocationIdPropertyName);
                            }
                            else if (memberName.SequenceEqual(StreamIdPropertyNameUtf8))
                            {
                                streamId = JsonUtils.ReadAsString(reader, StreamIdPropertyName);
                            }
                            else if (memberName.SequenceEqual(TargetPropertyNameUtf8))
                            {
                                target = JsonUtils.ReadAsString(reader, TargetPropertyName);
                            }
                            else if (memberName.SequenceEqual(ErrorPropertyNameUtf8))
                            {
                                error = JsonUtils.ReadAsString(reader, ErrorPropertyName);
                            }
                            else if (memberName.SequenceEqual(ResultPropertyNameUtf8))
                            {
                                hasResult = true;

                                if (string.IsNullOrEmpty(invocationId))
                                {
                                    JsonUtils.CheckRead(reader);

                                    // If we don't have an invocation id then we need to store it as a JToken so we can parse it later
                                    resultToken = JToken.Load(reader);
                                }
                                else
                                {
                                    // If we have an invocation id already we can parse the end result
                                    var returnType = binder.GetReturnType(invocationId);

                                    if (!JsonUtils.ReadForType(reader, returnType))
                                    {
                                        throw new JsonReaderException("Unexpected end when reading JSON");
                                    }

                                    result = PayloadSerializer.Deserialize(reader, returnType);
                                }
                            }
                            else if (memberName.SequenceEqual(ItemPropertyNameUtf8))
                            {
                                JsonUtils.CheckRead(reader);

                                hasItem = true;

                                string id = null;
                                if (!string.IsNullOrEmpty(invocationId))
                                {
                                    id = invocationId;
                                }
                                else if (!string.IsNullOrEmpty(streamId))
                                {
                                    id = streamId;
                                }
                                else
                                {
                                    // If we don't have an id yetmthen we need to store it as a JToken to parse later
                                    itemToken = JToken.Load(reader);
                                    break;
                                }

                                Type itemType = binder.GetStreamItemType(id);

                                try
                                {
                                    item = PayloadSerializer.Deserialize(reader, itemType);
                                }
                                catch (JsonSerializationException ex)
                                {
                                    return new StreamBindingFailureMessage(id, ExceptionDispatchInfo.Capture(ex));
                                }
                            }
                            else if (memberName.SequenceEqual(ArgumentsPropertyNameUtf8))
                            {
                                JsonUtils.CheckRead(reader);

                                int initialDepth = reader.Depth;
                                if (reader.TokenType != JsonTokenType.StartArray)
                                {
                                    throw new InvalidDataException($"Expected '{ArgumentsPropertyName}' to be of type {JTokenType.Array}.");
                                }

                                hasArguments = true;

                                if (string.IsNullOrEmpty(target))
                                {
                                    // We don't know the method name yet so just parse an array of generic JArray
                                    argumentsToken = JArray.Load(reader);
                                }
                                else
                                {
                                    try
                                    {
                                        var paramTypes = binder.GetParameterTypes(target);
                                        arguments = BindArguments(reader, paramTypes);
                                    }
                                    catch (Exception ex)
                                    {
                                        argumentBindingException = ExceptionDispatchInfo.Capture(ex);

                                        // Could be at any point in argument array JSON when an error is thrown
                                        // Read until the end of the argument JSON array
                                        while (reader.Depth == initialDepth && reader.TokenType == JsonTokenType.StartArray ||
                                                reader.Depth > initialDepth)
                                        {
                                            JsonUtils.CheckRead(reader);
                                        }
                                    }
                                }
                            }
                            else if (memberName.SequenceEqual(HeadersPropertyNameUtf8))
                            {
                                JsonUtils.CheckRead(reader);
                                headers = ReadHeaders(reader);
                            }
                            else
                            {
                                // Skip read the property name
                                JsonUtils.CheckRead(reader);
                                // Skip the value for this property
                                JsonUtils.Skip(reader);
                            }
                            break;
                        case JsonTokenType.EndObject:
                            completed = true;
                            break;
                    }
                }
                while (!completed && JsonUtils.CheckRead(reader));

                HubMessage message;

                switch (type)
                {
                    case HubProtocolConstants.InvocationMessageType:
                        {
                            if (argumentsToken != null)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    arguments = BindArguments(argumentsToken, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindInvocationMessage(invocationId, target, arguments, hasArguments, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamInvocationMessageType:
                        {
                            if (argumentsToken != null)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    arguments = BindArguments(argumentsToken, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindStreamInvocationMessage(invocationId, target, arguments, hasArguments, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamDataMessageType:
                        if (itemToken != null)
                        {
                            var itemType = binder.GetStreamItemType(streamId);
                            try
                            {
                                item = itemToken.ToObject(itemType, PayloadSerializer);
                            }
                            catch (JsonSerializationException ex)
                            {
                                return new StreamBindingFailureMessage(streamId, ExceptionDispatchInfo.Capture(ex));
                            }
                        }
                        message = BindParamStreamMessage(streamId, item, hasItem, binder);
                        break;
                    case HubProtocolConstants.StreamItemMessageType:
                        if (itemToken != null)
                        {
                            var returnType = binder.GetStreamItemType(invocationId);
                            try
                            {
                                item = itemToken.ToObject(returnType, PayloadSerializer);
                            }
                            catch (JsonSerializationException ex)
                            {
                                return new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
                            };
                        }

                        message = BindStreamItemMessage(invocationId, item, hasItem, binder);
                        break;
                    case HubProtocolConstants.CompletionMessageType:
                        if (resultToken != null)
                        {
                            var returnType = binder.GetReturnType(invocationId);
                            result = resultToken.ToObject(returnType, PayloadSerializer);
                        }

                        message = BindCompletionMessage(invocationId, error, result, hasResult, binder);
                        break;
                    case HubProtocolConstants.CancelInvocationMessageType:
                        message = BindCancelInvocationMessage(invocationId);
                        break;
                    case HubProtocolConstants.PingMessageType:
                        return PingMessage.Instance;
                    case HubProtocolConstants.CloseMessageType:
                        return BindCloseMessage(error);
                    case HubProtocolConstants.StreamCompleteMessageType:
                        message = BindStreamCompleteMessage(streamId, error);
                        break;
                    case null:
                        throw new InvalidDataException($"Missing required property '{TypePropertyName}'.");
                    default:
                        // Future protocol changes can add message types, old clients can ignore them
                        return null;
                }

                return ApplyHeaders(message, headers);
            }
            catch (JsonReaderException jrex)
            {
                throw new InvalidDataException("Error reading JSON.", jrex);
            }
        }

        private Dictionary<string, string> ReadHeaders(JsonTextReader reader)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);

            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new InvalidDataException($"Expected '{HeadersPropertyName}' to be of type {JTokenType.Object}.");
            }

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        var propertyName = reader.Value.ToString();

                        JsonUtils.CheckRead(reader);

                        if (reader.TokenType != JsonToken.String)
                        {
                            throw new InvalidDataException($"Expected header '{propertyName}' to be of type {JTokenType.String}.");
                        }

                        headers[propertyName] = reader.Value?.ToString();
                        break;
                    case JsonToken.Comment:
                        break;
                    case JsonToken.EndObject:
                        return headers;
                }
            }

            throw new JsonReaderException("Unexpected end when reading message headers");
        }

        private void WriteMessageCore(HubMessage message, IBufferWriter<byte> stream)
        {
            Utf8JsonWriter<IBufferWriter<byte>> writer = Utf8JsonWriter.Create(stream);

            writer.WriteObjectStart();
            switch (message)
            {
                case InvocationMessage m:
                    WriteMessageType(writer, HubProtocolConstants.InvocationMessageType);
                    WriteHeaders(writer, m);
                    WriteInvocationMessage(m, writer);
                    break;
                case StreamInvocationMessage m:
                    WriteMessageType(writer, HubProtocolConstants.StreamInvocationMessageType);
                    WriteHeaders(writer, m);
                    WriteStreamInvocationMessage(m, writer);
                    break;
                case StreamDataMessage m:
                    WriteMessageType(writer, HubProtocolConstants.StreamDataMessageType);
                    WriteStreamDataMessage(m, writer);
                    break;
                case StreamItemMessage m:
                    WriteMessageType(writer, HubProtocolConstants.StreamItemMessageType);
                    WriteHeaders(writer, m);
                    WriteStreamItemMessage(m, writer);
                    break;
                case CompletionMessage m:
                    WriteMessageType(writer, HubProtocolConstants.CompletionMessageType);
                    WriteHeaders(writer, m);
                    WriteCompletionMessage(m, writer);
                    break;
                case CancelInvocationMessage m:
                    WriteMessageType(writer, HubProtocolConstants.CancelInvocationMessageType);
                    WriteHeaders(writer, m);
                    WriteCancelInvocationMessage(m, writer);
                    break;
                case PingMessage _:
                    WriteMessageType(writer, HubProtocolConstants.PingMessageType);
                    break;
                case CloseMessage m:
                    WriteMessageType(writer, HubProtocolConstants.CloseMessageType);
                    WriteCloseMessage(m, writer);
                    break;
                case StreamCompleteMessage m:
                    WriteMessageType(writer, HubProtocolConstants.StreamCompleteMessageType);
                    WriteStreamCompleteMessage(m, writer);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported message type: {message.GetType().FullName}");
            }
            writer.WriteObjectEnd();
            writer.Flush();
        }

        private void WriteHeaders(Utf8JsonWriter<IBufferWriter<byte>> writer, HubInvocationMessage message)
        {
            if (message.Headers != null && message.Headers.Count > 0)
            {
                writer.WriteObjectStart(HeadersPropertyName);
                foreach (var value in message.Headers)
                {
                    writer.WriteAttribute(value.Key, value.Value);
                }
                writer.WriteObjectEnd();
            }
        }

        private void WriteCompletionMessage(CompletionMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            WriteInvocationId(message, writer);
            if (!string.IsNullOrEmpty(message.Error))
            {
                writer.WriteAttribute(ErrorPropertyName, message.Error);
            }
            else if (message.HasResult)
            {
                writer.WritePropertyName(ResultPropertyName);
                PayloadSerializer.Serialize(writer, message.Result);
            }
        }

        private void WriteCancelInvocationMessage(CancelInvocationMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            WriteInvocationId(message, writer);
        }

        private void WriteStreamCompleteMessage(StreamCompleteMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            writer.WriteAttribute(StreamIdPropertyName, message.StreamId);

            if (message.Error != null)
            {
                writer.WriteAttribute(ErrorPropertyName, message.Error);
            }
        }

        private void WriteStreamItemMessage(StreamItemMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            WriteInvocationId(message, writer);
            writer.WritePropertyName(ItemPropertyName);
            PayloadSerializer.Serialize(writer, message.Item);
        }

        private void WriteStreamDataMessage(StreamDataMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            writer.WriteAttribute(StreamIdPropertyName, message.StreamId);
            writer.WritePropertyName(ItemPropertyName);
            PayloadSerializer.Serialize(writer, message.Item);
        }

        private void WriteInvocationMessage(InvocationMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            WriteInvocationId(message, writer);
            writer.WriteAttribute(TargetPropertyName, message.Target);

            WriteArguments(message.Arguments, writer);
        }

        private void WriteStreamInvocationMessage(StreamInvocationMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            WriteInvocationId(message, writer);
            writer.WriteAttribute(TargetPropertyName, message.Target);

            WriteArguments(message.Arguments, writer);
        }

        private void WriteCloseMessage(CloseMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            if (message.Error != null)
            {
                writer.WriteAttribute(ErrorPropertyName, message.Error);
            }
        }

        private void WriteArguments(object[] arguments, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            writer.WriteArrayStart(ArgumentsPropertyName);
            foreach (var argument in arguments)
            {
                PayloadSerializer.Serialize(writer, argument);
            }
            writer.WriteArrayEnd();
        }

        private static void WriteInvocationId(HubInvocationMessage message, Utf8JsonWriter<IBufferWriter<byte>> writer)
        {
            if (!string.IsNullOrEmpty(message.InvocationId))
            {
                writer.WriteAttribute(InvocationIdPropertyName, message.InvocationId);
            }
        }

        private static void WriteMessageType(Utf8JsonWriter<IBufferWriter<byte>> writer, int type)
        {
            writer.WriteAttribute(TypePropertyName, type);
        }

        private HubMessage BindCancelInvocationMessage(string invocationId)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            return new CancelInvocationMessage(invocationId);
        }

        private HubMessage BindStreamCompleteMessage(string streamId, string error)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                throw new InvalidDataException($"Missing required property '{StreamIdPropertyName}'.");
            }

            // note : if the stream completes normally, the error should be `null`
            return new StreamCompleteMessage(streamId, error);
        }

        private HubMessage BindCompletionMessage(string invocationId, string error, object result, bool hasResult, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (error != null && hasResult)
            {
                throw new InvalidDataException("The 'error' and 'result' properties are mutually exclusive.");
            }

            if (hasResult)
            {
                return new CompletionMessage(invocationId, error, result, hasResult: true);
            }

            return new CompletionMessage(invocationId, error, result: null, hasResult: false);
        }

        private HubMessage BindParamStreamMessage(string streamId, object item, bool hasItem, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                throw new InvalidDataException($"Missing required property '{StreamIdPropertyName}");
            }
            if (!hasItem)
            {
                throw new InvalidDataException($"Missing required property '{ItemPropertyName}");
            }

            return new StreamDataMessage(streamId, item);
        }

        private HubMessage BindStreamItemMessage(string invocationId, object item, bool hasItem, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (!hasItem)
            {
                throw new InvalidDataException($"Missing required property '{ItemPropertyName}'.");
            }

            return new StreamItemMessage(invocationId, item);
        }

        private HubMessage BindStreamInvocationMessage(string invocationId, string target, object[] arguments, bool hasArguments, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (!hasArguments)
            {
                throw new InvalidDataException($"Missing required property '{ArgumentsPropertyName}'.");
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidDataException($"Missing required property '{TargetPropertyName}'.");
            }

            return new StreamInvocationMessage(invocationId, target, arguments);
        }

        private HubMessage BindInvocationMessage(string invocationId, string target, object[] arguments, bool hasArguments, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidDataException($"Missing required property '{TargetPropertyName}'.");
            }

            if (!hasArguments)
            {
                throw new InvalidDataException($"Missing required property '{ArgumentsPropertyName}'.");
            }

            return new InvocationMessage(invocationId, target, arguments);
        }

        private bool ReadArgumentAsType(JsonTextReader reader, IReadOnlyList<Type> paramTypes, int paramIndex)
        {
            if (paramIndex < paramTypes.Count)
            {
                var paramType = paramTypes[paramIndex];

                return JsonUtils.ReadForType(reader, paramType);
            }

            return reader.Read();
        }

        private object[] BindArguments(JsonTextReader reader, IReadOnlyList<Type> paramTypes)
        {
            object[] arguments = null;
            var paramIndex = 0;
            var argumentsCount = 0;
            var paramCount = paramTypes.Count;

            while (ReadArgumentAsType(reader, paramTypes, paramIndex))
            {
                if (reader.TokenType == JsonToken.EndArray)
                {
                    if (argumentsCount != paramCount)
                    {
                        throw new InvalidDataException($"Invocation provides {argumentsCount} argument(s) but target expects {paramCount}.");
                    }

                    return arguments ?? Array.Empty<object>();
                }

                if (arguments == null)
                {
                    arguments = new object[paramCount];
                }

                try
                {
                    if (paramIndex < paramCount)
                    {
                        arguments[paramIndex] = PayloadSerializer.Deserialize(reader, paramTypes[paramIndex]);
                    }
                    else
                    {
                        reader.Skip();
                    }

                    argumentsCount++;
                    paramIndex++;
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
                }
            }

            throw new JsonReaderException("Unexpected end when reading JSON");
        }

        private CloseMessage BindCloseMessage(string error)
        {
            // An empty string is still an error
            if (error == null)
            {
                return CloseMessage.Empty;
            }

            var message = new CloseMessage(error);
            return message;
        }

        private object[] BindArguments(JArray args, IReadOnlyList<Type> paramTypes)
        {
            var paramCount = paramTypes.Count;
            var argCount = args.Count;
            if (paramCount != argCount)
            {
                throw new InvalidDataException($"Invocation provides {argCount} argument(s) but target expects {paramCount}.");
            }

            if (paramCount == 0)
            {
                return Array.Empty<object>();
            }

            var arguments = new object[argCount];

            try
            {
                for (var i = 0; i < paramCount; i++)
                {
                    var paramType = paramTypes[i];
                    arguments[i] = args[i].ToObject(paramType, PayloadSerializer);
                }

                return arguments;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
            }
        }

        private HubMessage ApplyHeaders(HubMessage message, Dictionary<string, string> headers)
        {
            if (headers != null && message is HubInvocationMessage invocationMessage)
            {
                invocationMessage.Headers = headers;
            }

            return message;
        }

        internal static JsonSerializerSettings CreateDefaultSerializerSettings()
        {
            return new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        }
    }
}
