﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Text.Json.Serialization.Converters
{
    internal sealed class SByteConverter : JsonConverter<sbyte>
    {
        public override sbyte Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetSByte();
        }

        public override void Write(Utf8JsonWriter writer, sbyte value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
