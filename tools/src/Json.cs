// Minimal indented JSON writer plus a JavaScriptSerializer-backed reader.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

static class Json
{
    public static object Read(string text)
    {
        var s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        return s.DeserializeObject(text);
    }

    public static string Write(object value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, 0);
        return sb.Append('\n').ToString();
    }

    static void WriteValue(StringBuilder sb, object v, int indent)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is string) { WriteString(sb, (string)v); return; }
        if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
        if (v is double || v is float || v is decimal)
        {
            sb.Append(Convert.ToDouble(v).ToString("0.###", CultureInfo.InvariantCulture));
            return;
        }
        if (v is int || v is long || v is uint || v is short || v is ushort || v is byte)
        {
            sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
            return;
        }
        var dict = v as IDictionary;
        if (dict != null)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            int i = 0;
            foreach (DictionaryEntry kv in dict)
            {
                sb.Append(' ', indent + 2);
                WriteString(sb, kv.Key.ToString());
                sb.Append(": ");
                WriteValue(sb, kv.Value, indent + 2);
                if (++i < dict.Count) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(' ', indent).Append('}');
            return;
        }
        var list = v as IEnumerable;
        if (list != null)
        {
            var items = new List<object>();
            foreach (var o in list) items.Add(o);
            if (items.Count == 0) { sb.Append("[]"); return; }
            // Short scalar lists stay on one line; everything else is one item per line.
            bool scalar = items.TrueForAll(o => o == null || o is string || o.GetType().IsPrimitive);
            if (scalar)
            {
                sb.Append('[');
                for (int j = 0; j < items.Count; j++) { if (j > 0) sb.Append(", "); WriteValue(sb, items[j], indent); }
                sb.Append(']');
                return;
            }
            sb.Append("[\n");
            for (int j = 0; j < items.Count; j++)
            {
                sb.Append(' ', indent + 2);
                WriteValue(sb, items[j], indent + 2);
                if (j < items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(' ', indent).Append(']');
            return;
        }
        WriteString(sb, v.ToString());
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
