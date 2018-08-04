// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.JsonLab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.AspNetCore.Internal
{
    internal static class JsonUtils
    {
        internal static JsonTextReader CreateJsonTextReader(TextReader textReader)
        {
            var reader = new JsonTextReader(textReader);
            reader.ArrayPool = JsonArrayPool<char>.Shared;

            // Don't close the input, leave closing to the caller
            reader.CloseInput = false;

            return reader;
        }

        internal static JsonTextWriter CreateJsonTextWriter(TextWriter textWriter)
        {
            var writer = new JsonTextWriter(textWriter);
            writer.ArrayPool = JsonArrayPool<char>.Shared;
            // Don't close the output, leave closing to the caller
            writer.CloseOutput = false;

            // SignalR will always write a complete JSON response
            // This setting will prevent an error during writing be hidden by another error writing on dispose
            writer.AutoCompleteOnClose = false;

            return writer;
        }

        public static JObject GetObject(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
            {
                throw new InvalidDataException($"Unexpected JSON Token Type '{token?.Type}'. Expected a JSON Object.");
            }

            return (JObject)token;
        }

        public static T GetOptionalProperty<T>(JObject json, string property, JTokenType expectedType = JTokenType.None, T defaultValue = default)
        {
            var prop = json[property];

            if (prop == null)
            {
                return defaultValue;
            }

            return GetValue<T>(property, expectedType, prop);
        }

        public static T GetRequiredProperty<T>(JObject json, string property, JTokenType expectedType = JTokenType.None)
        {
            var prop = json[property];

            if (prop == null)
            {
                throw new InvalidDataException($"Missing required property '{property}'.");
            }

            return GetValue<T>(property, expectedType, prop);
        }

        public static T GetValue<T>(string property, JTokenType expectedType, JToken prop)
        {
            if (expectedType != JTokenType.None && prop.Type != expectedType)
            {
                throw new InvalidDataException($"Expected '{property}' to be of type {expectedType}.");
            }
            return prop.Value<T>();
        }

        public static string GetTokenString(JsonTokenType tokenType)
        {
            switch (tokenType)
            {
                case JsonTokenType.None:
                    break;
                case JsonTokenType.StartObject:
                    return JTokenType.Object.ToString();
                case JsonTokenType.StartArray:
                    return JTokenType.Array.ToString();
                case JsonTokenType.PropertyName:
                    return JTokenType.Property.ToString();
                default:
                    break;
            }
            return tokenType.ToString();
        }

        public static string GetTokenString(JsonToken tokenType)
        {
            switch (tokenType)
            {
                case JsonToken.None:
                    break;
                case JsonToken.StartObject:
                    return JTokenType.Object.ToString();
                case JsonToken.StartArray:
                    return JTokenType.Array.ToString();
                case JsonToken.PropertyName:
                    return JTokenType.Property.ToString();
                default:
                    break;
            }
            return tokenType.ToString();
        }

        public static void EnsureObjectStart(Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException($"Unexpected JSON Token Type '{GetTokenString(reader.TokenType)}'. Expected a JSON Object.");
            }
        }

        public static void EnsureArrayStart(Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new InvalidDataException($"Unexpected JSON Token Type '{GetTokenString(reader.TokenType)}'. Expected a JSON Array.");
            }
        }

        public static void EnsureObjectStart(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new InvalidDataException($"Unexpected JSON Token Type '{GetTokenString(reader.TokenType)}'. Expected a JSON Object.");
            }
        }

        public static void EnsureArrayStart(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.StartArray)
            {
                throw new InvalidDataException($"Unexpected JSON Token Type '{GetTokenString(reader.TokenType)}'. Expected a JSON Array.");
            }
        }

        public static int? ReadAsInt32(JsonTextReader reader, string propertyName)
        {
            reader.Read();

            if (reader.TokenType != JsonToken.Integer)
            {
                throw new InvalidDataException($"Expected '{propertyName}' to be of type {JTokenType.Integer}.");
            }

            if (reader.Value == null)
            {
                return null;
            }

            return Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
        }

        public static int? ReadAsInt32(Utf8JsonReader reader, string propertyName)
        {
            reader.Read();

            if (reader.TokenType != JsonTokenType.Value && reader.ValueType != JsonValueType.Number)
            {
                throw new InvalidDataException($"Expected '{propertyName}' to be of type {JTokenType.Integer}.");
            }

            if (reader.Value.IsEmpty)
            {
                return null;
            }
            if (!Utf8Parser.TryParse(reader.Value, out int value, out _))
            {
                throw new InvalidDataException($"Expected '{propertyName}' to be of type {JTokenType.Integer}.");
            }
            return value;
        }

        public static string ReadAsString(JsonTextReader reader, string propertyName)
        {
            reader.Read();

            if (reader.TokenType != JsonToken.String)
            {
                throw new InvalidDataException($"Expected '{propertyName}' to be of type {JTokenType.String}.");
            }

            return reader.Value?.ToString();
        }

        public static unsafe string ReadAsString(Utf8JsonReader reader, string propertyName)
        {
            reader.Read();

            if (reader.TokenType != JsonTokenType.Value && reader.ValueType != JsonValueType.String)
            {
                throw new InvalidDataException($"Expected '{propertyName}' to be of type {JTokenType.String}.");
            }

            if (reader.Value.IsEmpty) return null;

#if NETCOREAPP2_2
            return Encoding.UTF8.GetString(reader.Value);
#else
            fixed (byte* bytes = &MemoryMarshal.GetReference(reader.Value))
            {
                return Encoding.UTF8.GetString(bytes, reader.Value.Length);
            }
#endif
        }

        public static bool CheckRead(JsonTextReader reader)
        {
            if (!reader.Read())
            {
                throw new InvalidDataException("Unexpected end when reading JSON.");
            }

            return true;
        }

        public static bool IsStartToken(JsonTokenType token)
        {
            switch (token)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    return true;
                default:
                    return false;
            }
        }

        public static void Skip(Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                reader.Read();
            }

            if (IsStartToken(reader.TokenType))
            {
                //int depth = reader.Depth;
                while (reader.Read())
                {
                }
            }
        }

        public static bool CheckRead(Utf8JsonReader reader)
        {
            if (!reader.Read())
            {
                throw new InvalidDataException("Unexpected end when reading JSON.");
            }

            return true;
        }

        public static bool ReadForType(JsonTextReader reader, Type type)
        {
            // Explicity read values as dates from JSON with reader.
            // We do this because otherwise dates are read as strings
            // and the JsonSerializer will use a conversion method that won't
            // preserve UTC in DateTime.Kind for UTC ISO8601 dates
            if (type == typeof(DateTime) || type == typeof(DateTime?))
            {
                reader.ReadAsDateTime();
            }
            else if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
            {
                reader.ReadAsDateTimeOffset();
            }
            else
            {
                reader.Read();
            }

            // TokenType will be None if there is no more content
            return reader.TokenType != JsonToken.None;
        }

        private class JsonArrayPool<T> : IArrayPool<T>
        {
            private readonly ArrayPool<T> _inner;

            internal static readonly JsonArrayPool<T> Shared = new JsonArrayPool<T>(ArrayPool<T>.Shared);

            public JsonArrayPool(ArrayPool<T> inner)
            {
                _inner = inner;
            }

            public T[] Rent(int minimumLength)
            {
                return _inner.Rent(minimumLength);
            }

            public void Return(T[] array)
            {
                _inner.Return(array);
            }
        }
    }
}
