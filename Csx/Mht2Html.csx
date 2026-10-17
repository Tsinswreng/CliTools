#r "nuget: Tsinswreng.CsSh, 0.3.0-alpha"
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tsinswreng.CsSh;
using static Tsinswreng.CsSh.ShGlobal;
using CT = System.Threading.CancellationToken;

// ============================================================================
// Mht2Html —— 把 MIME HTML（.mht / .mhtml）轉成單一自包含 .html
// ============================================================================
// 用法:
//   dotnet script Mht2Html.csx -- <輸入.mht> [更多輸入...] [選項]
//   dotnet script Mht2Html.csx -- --help
//
// 流程:
//   1. 解析 MIME：多層 multipart（related / alternative）遞迴展開，
//      base64 與 quoted-printable 兩種傳輸編碼都解，支援 CRLF 與純 LF。
//   2. 建資源索引：以 Content-Location 與 Content-ID 為鍵，比對時依次退回
//      去 fragment、換 http/https、只比檔名。
//   3. 走一遍 HTML 標籤（自寫解析器，不做 DOM 重排，未改動的標籤原樣保留）：
//      <link rel=stylesheet> 就地換成 <style>；<img>/<source>/<video poster>/
//      <object data>/SVG image 的 src 與 srcset 換成 base64 data URI；
//      style 屬性與 <style> 區塊內的 url() 一併內嵌；iframe 指向的子框架 html
//      遞迴轉換後內嵌成 data:text/html；延遲載入屬性可用時補進 src。
//   4. CSS：@charset 移除、@import 就地展開、url() 換成 data URI、
//      CSS 轉義（\/、\:、\2f）先還原再比對。
//   5. 輸出統一 UTF-8，並把文件原有的 charset 宣告就地改成 utf-8。
//
// 明確不做：不執行腳本、不改寫 <a href>、不壓縮空白、不重排文件結構。
// 取不到的遠端引用（存檔時就沒抓到的字型、圖示）保留原樣，只計入統計。
// ============================================================================

CT Ct = default;

var Options = Cli.Parse(Args);
if(Options.ShowHelp){
	Cli.PrintUsage();
	return;
}
if(Options.Inputs.Count == 0){
	Cli.PrintUsage();
	Environment.ExitCode = 1;
	return;
}
var FailCount = 0;
foreach(var InArg in Options.Inputs){
	if(!Cli.ConvertOne(InArg, Options)){
		FailCount++;
	}
}
if(FailCount > 0){
	Environment.ExitCode = FailCount;
}

// ============================================================================
// 命令列
// ============================================================================
sealed class Opts {
	public readonly List<string> Inputs = new();
	public string? OutFile;
	public string? OutDir;
	public bool StripScripts;
	public bool Inspect;
	public bool Quiet;
	public bool ShowHelp;
}

static class Cli {
	public static Opts Parse(IEnumerable<string> Raw){
		var O = new Opts();
		var Args = Raw.ToList();
		for(var I = 0; I < Args.Count; I++){
			var A = Args[I];
			switch(A){
				case "-h":
				case "--help":
					O.ShowHelp = true;
					return O;
				case "-o":
				case "--out":
					if(I + 1 >= Args.Count){ throw new ArgumentException("-o 需要一個輸出路徑"); }
					O.OutFile = Args[++I];
					continue;
				case "--out-dir":
					if(I + 1 >= Args.Count){ throw new ArgumentException("--out-dir 需要一個目錄"); }
					O.OutDir = Args[++I];
					continue;
				case "--strip-scripts":
					O.StripScripts = true;
					continue;
				case "--inspect":
					O.Inspect = true;
					continue;
				case "-q":
				case "--quiet":
					O.Quiet = true;
					continue;
			}
			if(A.StartsWith("-")){
				throw new ArgumentException("未知旗標: " + A);
			}
			O.Inputs.Add(A);
		}
		return O;
	}

	public static void PrintUsage(){
		Echo("Mht2Html：把 MIME HTML（.mht / .mhtml）轉成單一自包含 .html 檔。");
		Echo("");
		Echo("用法:");
		Echo("  dotnet script Mht2Html.csx -- <輸入.mht> [更多輸入...] [選項]");
		Echo("");
		Echo("選項:");
		Echo("  -o, --out <檔案>       指定輸出檔名（僅限單一輸入）");
		Echo("  --out-dir <目錄>       指定輸出目錄（檔名沿用輸入檔名，副檔名改 .html）");
		Echo("  --strip-scripts        移除所有 script 元素");
		Echo("  --inspect              只列出 MIME 結構，不轉換");
		Echo("  -q, --quiet            只輸出錯誤");
		Echo("  -h, --help             顯示本說明");
		Echo("");
		Echo("預設行為：輸入 1 個 mht，輸出同名 .html（與輸入同目錄），");
		Echo("圖片、字型、CSS 全部內嵌為 base64 data URI，輸出檔不依賴任何外部資源。");
	}

	public static bool ConvertOne(string InArg, Opts O){
		string InPath;
		try {
			InPath = FullPath(InArg).Value;
		} catch(Exception Ex) {
			Console.Error.WriteLine("[error] 無法解析路徑 " + InArg + ": " + Ex.Message);
			return false;
		}
		if(!File.Exists(InPath)){
			Console.Error.WriteLine("[error] 檔案不存在: " + InPath);
			return false;
		}

		byte[] Raw;
		try {
			Raw = File.ReadAllBytes(InPath);
		} catch(Exception Ex) {
			Console.Error.WriteLine("[error] 讀取失敗 " + InPath + ": " + Ex.Message);
			return false;
		}

		if(O.Inspect){
			try {
				MhtConverter.Inspect(Raw, InPath);
				return true;
			} catch(Exception Ex) {
				Console.Error.WriteLine("[error] 解析失敗 " + InPath + ": " + Ex.Message);
				return false;
			}
		}

		MhtConvertResult R;
		try {
			R = MhtConverter.Convert(Raw, O.StripScripts);
		} catch(MhtFormatException Ex){
			Console.Error.WriteLine("[error] " + Ex.Message + "  (" + InPath + ")");
			return false;
		} catch(Exception Ex){
			Console.Error.WriteLine("[error] 轉換失敗 " + InPath + ": " + Ex.GetType().Name + ": " + Ex.Message);
			return false;
		}

		var OutPath = ResolveOutPath(InPath, O);
		try {
			var Dir = Path.GetDirectoryName(OutPath);
			if(!string.IsNullOrEmpty(Dir)){ Directory.CreateDirectory(Dir); }
			File.WriteAllBytes(OutPath, new UTF8Encoding(false).GetBytes(R.Html));
		} catch(Exception Ex) {
			Console.Error.WriteLine("[error] 寫入失敗 " + OutPath + ": " + Ex.Message);
			return false;
		}

		var UniqueWarn = R.Warnings.Distinct().ToList();
		if(!O.Quiet){
			foreach(var W in UniqueWarn.Take(5)){
				Console.Error.WriteLine("[warn] " + Path.GetFileName(InPath) + ": " + W);
			}
			if(UniqueWarn.Count > 5){
				Console.Error.WriteLine("[warn] …另有 " + (UniqueWarn.Count - 5) + " 類警告未列出");
			}
			var SizeBytes = new FileInfo(OutPath).Length;
			var SizeText = SizeBytes < 10 * 1024
				? SizeBytes + " B"
				: (SizeBytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
			var Parts = R.Warnings.Count == 0 ? "" : "  警告 " + UniqueWarn.Count + " 類";
			Echo($"[ok] {Path.GetFileName(InPath)} -> {Path.GetFileName(OutPath)}  "
				+ $"{SizeText}  資源 {R.ResourceCount} 個 (圖 {R.InlinedImages} 次 / CSS {R.InlinedCss} / 其他 {R.InlinedOther})  "
				+ $"未解析引用 {R.Unresolved}" + Parts);
			if(R.UnresolvedSamples.Count > 0){
				Echo("     未解析引用示例:");
				foreach(var S in R.UnresolvedSamples.Distinct().Take(5)){
					Echo("       " + S);
				}
			}
		}
		return true;
	}

	static string ResolveOutPath(string InPath, Opts O){
		var BaseName = Path.GetFileNameWithoutExtension(InPath) + ".html";
		if(!string.IsNullOrEmpty(O.OutFile)){
			if(O.Inputs.Count > 1){
				throw new ArgumentException("-o 只能搭配單一輸入；多輸入請改用 --out-dir");
			}
			return FullPath(O.OutFile).Value;
		}
		if(!string.IsNullOrEmpty(O.OutDir)){
			return Path.Combine(FullPath(O.OutDir).Value, BaseName);
		}
		var SameDir = Path.Combine(Path.GetDirectoryName(InPath) ?? ".", BaseName);
		if(string.Equals(SameDir, InPath, StringComparison.OrdinalIgnoreCase)){
			SameDir = Path.Combine(Path.GetDirectoryName(InPath) ?? ".", Path.GetFileNameWithoutExtension(InPath) + ".out.html");
		}
		return SameDir;
	}
}

// ============================================================================
// MIME 結構
// ============================================================================
sealed class MhtFormatException:Exception {
	public MhtFormatException(string Message):base(Message){}
}

sealed class MimeHeaders {
	readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);

	public void Add(string Name, string Value){
		if(Map.TryGetValue(Name, out var Old)){
			Map[Name] = Old + ", " + Value;
		} else {
			Map[Name] = Value;
		}
	}

	/// 續行（摺疊標頭）直接接在原文之後，不可插入分隔符，
	/// 否則 multipart 的 boundary 參數會變成 ", boundary" 而解析不到。
	public void Append(string Name, string Text){
		if(Map.TryGetValue(Name, out var Old)){
			Map[Name] = Old + Text;
		} else {
			Map[Name] = Text;
		}
	}

	public string? Get(string Name) => Map.TryGetValue(Name, out var V) ? V : null;

	public IEnumerable<string> Names => Map.Keys;
}

sealed class MimePart {
	public MimeHeaders Headers = new();
	public int BodyStart;
	public int BodyEnd;
	public string MediaType = "application/octet-stream";
	public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
	public string? TransferEncoding;
	public string? ContentLocation;
	public string? ContentId;
	public readonly List<MimePart> Children = new();
	byte[]? CachedDecoded;

	public bool IsMultipart => MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
	public bool IsHtml => MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
		|| MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
	public string? Charset => Params.TryGetValue("charset", out var V) ? V : null;

	public byte[] Decoded(byte[] Source){
		return CachedDecoded ??= Transfer.Decode(Transfer.Slice(Source, BodyStart, BodyEnd), TransferEncoding);
	}
}

static class MimeParser {
	public static MimePart Parse(byte[] Data){
		if(Data.Length == 0){ throw new MhtFormatException("檔案是空的"); }
		return ParseOne(Data, 0, Data.Length, 0);
	}

	static MimePart ParseOne(byte[] Data, int Start, int End, int Depth){
		if(Depth > 8){ throw new MhtFormatException("MIME 巢狀過深（超過 8 層）"); }
		var (Headers, BodyStart) = ReadHeaders(Data, Start, End);
		var P = new MimePart {
			Headers = Headers,
			BodyStart = BodyStart,
			BodyEnd = End,
			ContentLocation = NullIfEmpty(Headers.Get("Content-Location")),
			ContentId = NullIfEmpty(Headers.Get("Content-ID")),
			TransferEncoding = NullIfEmpty(Headers.Get("Content-Transfer-Encoding")),
		};
		var (MediaType, Params) = ContentType.Parse(Headers.Get("Content-Type"));
		P.MediaType = MediaType;
		P.Params = Params;
		if(P.IsMultipart){
			var Boundary = Params.TryGetValue("boundary", out var B) ? B : null;
			if(string.IsNullOrEmpty(Boundary)){
				throw new MhtFormatException("multipart 缺少 boundary 參數");
			}
			foreach(var (S, E) in Multipart.Split(Data, BodyStart, End, Boundary)){
				P.Children.Add(ParseOne(Data, S, E, Depth + 1));
			}
		}
		return P;
	}

	static (MimeHeaders, int) ReadHeaders(byte[] Data, int Start, int End){
		var Headers = new MimeHeaders();
		var I = Start;
		string? LastName = null;
		while(I < End){
			var LineEndRaw = Transfer.IndexOfLF(Data, I, End);
			var LineEnd = LineEndRaw < 0 ? End : LineEndRaw;
			var Stop = LineEnd;
			if(Stop > I && Data[Stop - 1] == (byte)'\r'){ Stop--; }
			if(Stop == I){
				// 空行：標頭結束
				return (Headers, Math.Min(LineEnd + 1, End));
			}
			var Text = Encoding.Latin1.GetString(Data, I, Stop - I);
			if((Text[0] == ' ' || Text[0] == '\t') && LastName != null){
				Headers.Append(LastName, Text);
			} else {
				var Colon = Text.IndexOf(':');
				if(Colon > 0){
					var Name = Text.Substring(0, Colon).Trim();
					var Value = Text.Substring(Colon + 1).Trim();
					Headers.Add(Name, Value);
					LastName = Name;
				} else if(Colon == 0){
					// 不合法的空標頭名，忽略
					LastName = null;
				} else {
					// 沒有冒號的行視為續行
					if(LastName != null){ Headers.Append(LastName, Text); }
				}
			}
			if(LineEndRaw < 0){ return (Headers, End); }
			I = LineEndRaw + 1;
		}
		return (Headers, End);
	}

	static string? NullIfEmpty(string? S) => string.IsNullOrWhiteSpace(S) ? null : S.Trim();
}

static class Multipart {
	/// 依 boundary 切出各個 part 的位元組範圍（不複製內容）。
	public static List<(int Start, int End)> Split(byte[] Data, int Start, int End, string Boundary){
		var Result = new List<(int, int)>();
		var Delim = Encoding.ASCII.GetBytes("--" + Boundary);
		var PartStart = -1;
		var I = Start;
		while(I < End){
			var LineStart = I == Start || Data[I - 1] == (byte)'\n';
			if(LineStart && Transfer.Matches(Data, I, End, Delim)){
				var After = I + Delim.Length;
				var Closed = After + 1 < End && Data[After] == (byte)'-' && Data[After + 1] == (byte)'-';
				if(PartStart >= 0){
					var Stop = I;
					if(Stop - 1 >= PartStart && Data[Stop - 1] == (byte)'\n'){
						Stop--;
						if(Stop - 1 >= PartStart && Data[Stop - 1] == (byte)'\r'){ Stop--; }
					} else if(Stop - 1 >= PartStart && Data[Stop - 1] == (byte)'\r'){
						Stop--;
					}
					Result.Add((PartStart, Stop));
					PartStart = -1;
				}
				if(Closed){ return Result; }
				var NewLine = Transfer.IndexOfLF(Data, After, End);
				if(NewLine < 0){ return Result; }
				PartStart = NewLine + 1;
				I = PartStart;
				continue;
			}
			var NextLine = Transfer.IndexOfLF(Data, I, End);
			if(NextLine < 0){ break; }
			I = NextLine + 1;
		}
		// 缺少結束分隔線時，容忍最後一個 part 延伸到結尾
		if(PartStart >= 0 && PartStart < End){ Result.Add((PartStart, End)); }
		return Result;
	}
}

static class ContentType {
	public static (string MediaType, Dictionary<string, string> Params) Parse(string? Raw){
		var Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if(string.IsNullOrWhiteSpace(Raw)){
			return ("text/plain", Params);
		}
		var Text = Raw.Trim();
		var Semi = Text.IndexOf(';');
		var MediaType = (Semi < 0 ? Text : Text.Substring(0, Semi)).Trim().ToLowerInvariant();
		if(MediaType.Length == 0){ MediaType = "application/octet-stream"; }
		if(Semi >= 0){
			var Rest = Text.Substring(Semi + 1);
			var I = 0;
			while(I < Rest.Length){
				while(I < Rest.Length && (Rest[I] == ';' || char.IsWhiteSpace(Rest[I]))){ I++; }
				var NameStart = I;
				while(I < Rest.Length && Rest[I] != '=' && Rest[I] != ';'){ I++; }
				var Name = Rest.Substring(NameStart, I - NameStart).Trim();
				string Value = "";
				if(I < Rest.Length && Rest[I] == '='){
					I++;
					while(I < Rest.Length && char.IsWhiteSpace(Rest[I])){ I++; }
					if(I < Rest.Length && (Rest[I] == '"' || Rest[I] == '\'')){
						var Quote = Rest[I];
						I++;
						var VStart = I;
						while(I < Rest.Length && Rest[I] != Quote){ I++; }
						Value = Rest.Substring(VStart, I - VStart);
						if(I < Rest.Length){ I++; }
					} else {
						var VStart = I;
						while(I < Rest.Length && Rest[I] != ';'){ I++; }
						Value = Rest.Substring(VStart, I - VStart).Trim();
					}
				}
				if(Name.Length > 0 && !Params.ContainsKey(Name)){ Params[Name] = Value; }
				while(I < Rest.Length && Rest[I] != ';'){ I++; }
			}
		}
		return (MediaType, Params);
	}
}

static class Transfer {
	public static int IndexOfLF(byte[] Data, int Start, int End){
		for(var I = Start; I < End; I++){
			if(Data[I] == (byte)'\n'){ return I; }
		}
		return -1;
	}

	public static bool Matches(byte[] Data, int At, int End, byte[] Pattern){
		if(At + Pattern.Length > End){ return false; }
		for(var I = 0; I < Pattern.Length; I++){
			if(Data[At + I] != Pattern[I]){ return false; }
		}
		return true;
	}

	public static byte[] Slice(byte[] Data, int Start, int End){
		if(Start >= End){ return Array.Empty<byte>(); }
		var Len = End - Start;
		var Out = new byte[Len];
		Buffer.BlockCopy(Data, Start, Out, 0, Len);
		return Out;
	}

	public static byte[] Decode(byte[] Body, string? EncodingName){
		var Name = (EncodingName ?? "").Trim().ToLowerInvariant();
		if(Name.StartsWith("base64")){
			return DecodeBase64(Body);
		}
		if(Name.StartsWith("quoted-printable")){
			return DecodeQuotedPrintable(Body);
		}
		// 7bit / 8bit / binary / 未指定：原樣
		return Body;
	}

	public static byte[] DecodeQuotedPrintable(byte[] Body){
		var Out = new List<byte>(Body.Length);
		var I = 0;
		while(I < Body.Length){
			var B = Body[I];
			if(B == (byte)'='){
				// 軟換行：= CRLF 或 = LF
				if(I + 1 < Body.Length && Body[I + 1] == (byte)'\r'){ I += 2; if(I < Body.Length && Body[I] == (byte)'\n'){ I++; } continue; }
				if(I + 1 < Body.Length && Body[I + 1] == (byte)'\n'){ I += 2; continue; }
				if(I + 2 < Body.Length){
					var H1 = HexVal(Body[I + 1]);
					var H2 = HexVal(Body[I + 2]);
					if(H1 >= 0 && H2 >= 0){
						Out.Add((byte)((H1 << 4) | H2));
						I += 3;
						continue;
					}
				}
				// 非法的 = 序列，原樣保留
				Out.Add(B);
				I++;
				continue;
			}
			Out.Add(B);
			I++;
		}
		return Out.ToArray();
	}

	public static byte[] DecodeBase64(byte[] Body){
		var Sb = new StringBuilder(Body.Length);
		foreach(var B in Body){
			if(B == (byte)'=' || (B >= (byte)'A' && B <= (byte)'Z') || (B >= (byte)'a' && B <= (byte)'z')
				|| (B >= (byte)'0' && B <= (byte)'9') || B == (byte)'+' || B == (byte)'/'){
				Sb.Append((char)B);
			}
			// 其餘字元（空白、換行、少見的雜訊）一律丟棄
		}
		var Clean = Sb.ToString();
		var Rem = Clean.Length % 4;
		if(Rem != 0){
			Clean = Clean.TrimEnd('=');
			Rem = Clean.Length % 4;
			if(Rem == 2){ Clean += "=="; }
			else if(Rem == 3){ Clean += "="; }
			else if(Rem == 1){ Clean = Clean.Substring(0, Clean.Length - 1); }
		}
		try {
			return Convert.FromBase64String(Clean);
		} catch(FormatException Ex) {
			throw new MhtFormatException("base64 內容無法解碼: " + Ex.Message);
		}
	}

	static int HexVal(byte B){
		if(B >= (byte)'0' && B <= (byte)'9'){ return B - (byte)'0'; }
		if(B >= (byte)'A' && B <= (byte)'F'){ return B - (byte)'A' + 10; }
		if(B >= (byte)'a' && B <= (byte)'f'){ return B - (byte)'a' + 10; }
		return -1;
	}
}

// ============================================================================
// 文字解碼
// ============================================================================
static class TextCodec {
	static readonly UTF8Encoding StrictUtf8 = new(false, true);
	static bool CodePagesRegistered;

	public static string Decode(byte[] Bytes, string? CharsetHint){
		if(Bytes.Length == 0){ return ""; }
		// BOM 優先
		if(Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF){
			return Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3);
		}
		if(Bytes.Length >= 2 && Bytes[0] == 0xFF && Bytes[1] == 0xFE){
			return Encoding.Unicode.GetString(Bytes, 2, Bytes.Length - 2);
		}
		if(Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF){
			return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
		}

		var Declared = NormalizeCharset(CharsetHint) ?? SniffMetaCharset(Bytes);
		if(Declared != null && LooksLikeUtf8Family(Declared) == false && TryGetEncoding(Declared, out var Enc)){
			return Enc.GetString(Bytes);
		}
		if(IsValidUtf8(Bytes)){
			return Encoding.UTF8.GetString(Bytes);
		}
		// UTF-8 不合法：多半是 GBK/GB18030 之類的中文頁面
		if(TryGetEncoding("gb18030", out var Gb)){
			return Gb.GetString(Bytes);
		}
		if(Declared != null && TryGetEncoding(Declared, out var Fallback)){
			return Fallback.GetString(Bytes);
		}
		return Encoding.Latin1.GetString(Bytes);
	}

	static bool LooksLikeUtf8Family(string Name){
		var N = NormalizeCharset(Name) ?? "";
		return N is "utf-8" or "utf8" or "unicode-1-1-utf-8" or "x-unicode20utf8";
	}

	public static bool IsValidUtf8(byte[] Bytes){
		try {
			StrictUtf8.GetString(Bytes);
			return true;
		} catch(DecoderFallbackException){
			return false;
		}
	}

	public static string? NormalizeCharset(string? Raw){
		if(string.IsNullOrWhiteSpace(Raw)){ return null; }
		var S = Raw.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
		if(S.Length == 0){ return null; }
		return S switch {
			"gb2312" or "gbk" or "gb_2312-80" or "x-gbk" or "cp936" => "gb18030",
			"utf8" => "utf-8",
			_ => S,
		};
	}

	static string? SniffMetaCharset(byte[] Bytes){
		var Len = Math.Min(Bytes.Length, 4096);
		var Head = Encoding.Latin1.GetString(Bytes, 0, Len);
		var M = Regex.Match(Head, @"charset\s*=\s*[""']?\s*([A-Za-z0-9_.:\-]+)", RegexOptions.IgnoreCase);
		return M.Success ? M.Groups[1].Value : null;
	}

	public static bool TryGetEncoding(string Name, out Encoding Enc){
		Enc = Encoding.UTF8;
		var N = NormalizeCharset(Name);
		if(N == null){ return false; }
		if(N == "utf-8"){ Enc = new UTF8Encoding(false, false); return true; }
		try {
			Enc = Encoding.GetEncoding(N);
			return true;
		} catch(ArgumentException){
			EnsureCodePages();
			try {
				Enc = Encoding.GetEncoding(N);
				return true;
			} catch(ArgumentException){
				return false;
			}
		}
	}

	/// .NET 內建不含 GBK 等碼頁，需註冊 CodePages provider。
	/// 這裡用反射載入共享框架裏的 System.Text.Encoding.CodePages，
	/// 一來腳本不必額外引用套件，二來 .NET 5 起該型別的建構子已不對外，
	/// 只能取靜態的 Instance 屬性。
	static void EnsureCodePages(){
		if(CodePagesRegistered){ return; }
		CodePagesRegistered = true;
		try {
			const string AsmName = "System.Text.Encoding.CodePages";
			const string TypeName = "System.Text.CodePagesEncodingProvider";
			var T = Type.GetType(TypeName + ", " + AsmName, false);
			if(T == null){
				T = System.Reflection.Assembly.Load(AsmName).GetType(TypeName);
			}
			if(T == null){ return; }
			object? Inst = null;
			var Prop = T.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			if(Prop != null){ Inst = Prop.GetValue(null); }
			if(Inst == null){
				Inst = Activator.CreateInstance(T, nonPublic: true);
			}
			if(Inst is EncodingProvider P){ Encoding.RegisterProvider(P); }
		} catch {
			// 拿不到就算了，後面還有 UTF-8 / Latin-1 兜底
		}
	}
}

// ============================================================================
// 資源索引
// ============================================================================
sealed class MhtRes {
	public byte[] Bytes = Array.Empty<byte>();
	public string MediaType = "application/octet-stream";
	public string? Charset;
	public string? BaseUrl;
	public string? ContentLocation;
	public string? Cid;

	public bool IsCss => MediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase);
	public bool IsHtml => MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
		|| MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

	public string DataUri {
		get { return "data:" + MediaType + ";base64," + Convert.ToBase64String(Bytes); }
	}

	public string Text(){
		return TextCodec.Decode(Bytes, Charset);
	}
}

sealed class ResourceIndex {
	readonly Dictionary<string, MhtRes> ByCid = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, MhtRes> ByUrl = new(StringComparer.Ordinal);
	readonly Dictionary<string, MhtRes> ByUrlLoose = new(StringComparer.OrdinalIgnoreCase);
	/// 最後手段：只按檔名比對，用來兜住 IE 存檔那種 file:/// 絕對路徑與相對引用對不上的情況。
	readonly Dictionary<string, MhtRes> ByLeaf = new(StringComparer.OrdinalIgnoreCase);
	readonly List<MhtRes> All = new();

	public int Count => All.Count;

	public void Add(MhtRes R){
		All.Add(R);
		if(!string.IsNullOrWhiteSpace(R.ContentLocation)){
			var Loc = R.ContentLocation.Trim();
			if(Loc.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)){
				var Cid = NormalizeCid(Loc.Substring(4));
				if(Cid.Length > 0 && !ByCid.ContainsKey(Cid)){ ByCid[Cid] = R; }
			} else {
				AddUrl(Loc, R);
			}
		}
		if(!string.IsNullOrWhiteSpace(R.Cid)){
			var Cid = NormalizeCid(R.Cid);
			if(Cid.Length > 0 && !ByCid.ContainsKey(Cid)){ ByCid[Cid] = R; }
		}
	}

	void AddUrl(string Url, MhtRes R){
		var Key = CanonicalUrl(Url);
		if(Key.Length == 0){ return; }
		if(!ByUrl.ContainsKey(Key)){ ByUrl[Key] = R; }
		if(!ByUrlLoose.ContainsKey(Key)){ ByUrlLoose[Key] = R; }
		var Leaf = LeafName(Key);
		if(Leaf.Length > 0 && !ByLeaf.ContainsKey(Leaf)){ ByLeaf[Leaf] = R; }
	}

	static string LeafName(string Url){
		if(Url.Length == 0){ return ""; }
		var End = Url.Length;
		var Q = Url.IndexOf('?');
		if(Q >= 0){ End = Q; }
		var H = Url.IndexOf('#');
		if(H >= 0 && H < End){ End = H; }
		var Slash = Url.LastIndexOf('/', End - 1);
		var Leaf = Slash < 0 ? Url.Substring(0, End) : Url.Substring(Slash + 1, End - Slash - 1);
		return Leaf.Trim();
	}

	public MhtRes? Find(string? Ref, string? BaseUrl){
		if(string.IsNullOrWhiteSpace(Ref)){ return null; }
		var R = Ref.Trim();
		if(R.Length == 0){ return null; }
		if(R.StartsWith("#")){ return null; }
		if(R.StartsWith("data:", StringComparison.OrdinalIgnoreCase)){ return null; }
		if(R.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)){
			var Cid = NormalizeCid(R.Substring(4));
			if(ByCid.TryGetValue(Cid, out var Hit)){ return Hit; }
			// 有的存檔把 cid 省略了尾部 @domain
			var At = Cid.IndexOf('@');
			if(At > 0 && ByCid.TryGetValue(Cid.Substring(0, At), out var Hit2)){ return Hit2; }
			return null;
		}
		var Abs = ToAbsolute(R, BaseUrl);
		if(Abs.Length == 0){ return null; }
		if(ByUrl.TryGetValue(Abs, out var Found)){ return Found; }
		if(ByUrlLoose.TryGetValue(Abs, out Found)){ return Found; }
		// 去掉 fragment 再試
		var Hash = Abs.IndexOf('#');
		if(Hash > 0){
			var NoFrag = Abs.Substring(0, Hash);
			if(ByUrl.TryGetValue(NoFrag, out Found)){ return Found; }
			if(ByUrlLoose.TryGetValue(NoFrag, out Found)){ return Found; }
		}
		// 極端情況：協定不一致（http 對 https）
		var Alt = Abs.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			? "https://" + Abs.Substring(7)
			: Abs.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
				? "http://" + Abs.Substring(8)
				: null;
		if(Alt != null){
			if(ByUrl.TryGetValue(Alt, out Found)){ return Found; }
			if(ByUrlLoose.TryGetValue(Alt, out Found)){ return Found; }
		}
		// 最後手段：只比檔名。IE 存檔常用 file:/// 絕對路徑而正文寫相對檔名。
		var Leaf = LeafName(Abs);
		if(Leaf.Length > 0 && ByLeaf.TryGetValue(Leaf, out Found)){ return Found; }
		return null;
	}

	public static string NormalizeCid(string Raw){
		var S = Raw.Trim();
		if(S.StartsWith("<")){ S = S.TrimStart('<'); }
		if(S.EndsWith(">")){ S = S.TrimEnd('>'); }
		return S.Trim();
	}

	static string CanonicalUrl(string Url){
		var S = Url.Trim();
		if(S.Length == 0){ return S; }
		if(S.StartsWith("file:", StringComparison.OrdinalIgnoreCase)){ return S; }
		if(Uri.TryCreate(S, UriKind.Absolute, out var U)){
			return U.AbsoluteUri.TrimEnd('/').Length == 0 ? U.AbsoluteUri : Canonicalize(U);
		}
		return S;
	}

	/// 統一成小寫協定與主機、保留原始路徑查詢，避免比對落空。
	static string Canonicalize(Uri U){
		var Sb = new StringBuilder();
		Sb.Append(U.Scheme.ToLowerInvariant()).Append("://").Append(U.Host.ToLowerInvariant());
		if(!U.IsDefaultPort){ Sb.Append(':').Append(U.Port); }
		Sb.Append(U.AbsolutePath);
		if(!string.IsNullOrEmpty(U.Query)){ Sb.Append(U.Query); }
		return Sb.ToString();
	}

	public static string ToAbsolute(string Ref, string? BaseUrl){
		var R = Ref.Trim();
		if(R.Length == 0){ return ""; }
		if(R.StartsWith("//")){ R = "https:" + R; }
		if(Uri.TryCreate(R, UriKind.Absolute, out var Abs)){ return CanonicalUrl(Abs.AbsoluteUri); }
		if(string.IsNullOrWhiteSpace(BaseUrl)){ return R; }
		if(Uri.TryCreate(BaseUrl, UriKind.Absolute, out var B)){
			if(Uri.TryCreate(B, R, out var Resolved)){ return CanonicalUrl(Resolved.AbsoluteUri); }
		}
		return R;
	}
}

// ============================================================================
// HTML 標籤解析
// ============================================================================
sealed class HtmlAttr {
	public string Name = "";
	public string NameLower = "";
	public string? Value;
	public int RawStart;
	public int RawEnd;
}

sealed class HtmlTag {
	public string Name = "";
	public string NameLower = "";
	public readonly List<HtmlAttr> Attrs = new();
	public bool SelfClosing;

	public HtmlAttr? Get(string LowerName){
		foreach(var A in Attrs){ if(A.NameLower == LowerName){ return A; } }
		return null;
	}

	public string? Value(string LowerName) => Get(LowerName)?.Value;
}

static class HtmlToken {
	/// 找出標籤結尾的 '>'，跳過屬性值內的引號內容。
	public static int FindTagEnd(string Html, int LtPos){
		var I = LtPos + 1;
		var N = Html.Length;
		while(I < N){
			var C = Html[I];
			if(C == '"' || C == '\''){
				var Quote = C;
				I++;
				while(I < N && Html[I] != Quote){ I++; }
				if(I < N){ I++; }
				continue;
			}
			if(C == '>'){ return I; }
			I++;
		}
		return -1;
	}

	public static HtmlTag Parse(string Html, int LtPos, int GtPos){
		var T = new HtmlTag();
		var N = GtPos;
		var I = LtPos + 1;
		var NameStart = I;
		while(I < N && !char.IsWhiteSpace(Html[I]) && Html[I] != '>' && Html[I] != '/'){
			I++;
		}
		T.Name = Html.Substring(NameStart, I - NameStart);
		T.NameLower = T.Name.ToLowerInvariant();
		while(I < N){
			while(I < N && char.IsWhiteSpace(Html[I])){ I++; }
			if(I >= N){ break; }
			if(Html[I] == '/'){
				T.SelfClosing = true;
				I++;
				continue;
			}
			if(Html[I] == '>'){ break; }
			var RawStart = I;
			while(I < N && !char.IsWhiteSpace(Html[I]) && Html[I] != '=' && Html[I] != '>' && Html[I] != '/'){ I++; }
			if(I == RawStart){
				I++;
				continue;
			}
			var A = new HtmlAttr {
				Name = Html.Substring(RawStart, I - RawStart),
				RawStart = RawStart,
			};
			A.NameLower = A.Name.ToLowerInvariant();
			while(I < N && char.IsWhiteSpace(Html[I])){ I++; }
			if(I < N && Html[I] == '='){
				I++;
				while(I < N && char.IsWhiteSpace(Html[I])){ I++; }
				if(I < N && (Html[I] == '"' || Html[I] == '\'')){
					var Quote = Html[I];
					I++;
					var VStart = I;
					while(I < N && Html[I] != Quote){ I++; }
					A.Value = HtmlEntity.Decode(Html.Substring(VStart, I - VStart));
					if(I < N){ I++; }
				} else {
					var VStart = I;
					while(I < N && !char.IsWhiteSpace(Html[I]) && Html[I] != '>'){ I++; }
					A.Value = HtmlEntity.Decode(Html.Substring(VStart, I - VStart));
				}
			}
			A.RawEnd = I;
			T.Attrs.Add(A);
		}
		return T;
	}
}

static class HtmlEntity {
	public static string Decode(string S){
		if(S.IndexOf('&') < 0){ return S; }
		var Sb = new StringBuilder(S.Length);
		var I = 0;
		while(I < S.Length){
			var C = S[I];
			if(C != '&'){ Sb.Append(C); I++; continue; }
			var Semi = S.IndexOf(';', I + 1);
			if(Semi < 0 || Semi - I > 12){ Sb.Append(C); I++; continue; }
			var Ent = S.Substring(I + 1, Semi - I - 1);
			var Text = ResolveEntity(Ent);
			if(Text == null){ Sb.Append(C); I++; continue; }
			Sb.Append(Text);
			I = Semi + 1;
		}
		return Sb.ToString();
	}

	static string? ResolveEntity(string Ent){
		if(Ent.Length == 0){ return null; }
		if(Ent[0] == '#'){
			int Code;
			if(Ent.Length > 1 && (Ent[1] == 'x' || Ent[1] == 'X')){
				if(!int.TryParse(Ent.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Code)){ return null; }
			} else {
				if(!int.TryParse(Ent.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out Code)){ return null; }
			}
			if(Code <= 0 || Code > 0x10FFFF){ return null; }
			return char.ConvertFromUtf32(Code);
		}
		return Ent switch {
			"amp" => "&",
			"lt" => "<",
			"gt" => ">",
			"quot" => "\"",
			"apos" => "'",
			"nbsp" => "\u00A0",
			"copy" => "\u00A9",
			"reg" => "\u00AE",
			"hellip" => "\u2026",
			"mdash" => "\u2014",
			"ndash" => "\u2013",
			"lsquo" => "\u2018",
			"rsquo" => "\u2019",
			"ldquo" => "\u201C",
			"rdquo" => "\u201D",
			"middot" => "\u00B7",
			"times" => "\u00D7",
			_ => null,
		};
	}

	public static string EncodeAttr(string S){
		var Sb = new StringBuilder(S.Length + 8);
		foreach(var C in S){
			switch(C){
				case '&': Sb.Append("&amp;"); break;
				case '<': Sb.Append("&lt;"); break;
				case '>': Sb.Append("&gt;"); break;
				case '"': Sb.Append("&quot;"); break;
				default: Sb.Append(C); break;
			}
		}
		return Sb.ToString();
	}
}

// ============================================================================
// CSS 內嵌
// ============================================================================
/// CSS 轉義序列（\2f、\/、\: 等）。有的站點會把資源網址轉義後才寫進 CSS，
/// 不還原就永遠對不上 Content-Location。
static class CssEscape {
	public static string Unescape(string S){
		if(S.IndexOf('\\') < 0){ return S; }
		var Sb = new StringBuilder(S.Length);
		var I = 0;
		while(I < S.Length){
			var C = S[I];
			if(C != '\\'){ Sb.Append(C); I++; continue; }
			I++;
			if(I >= S.Length){ break; }
			if(Uri.IsHexDigit(S[I])){
				var Start = I;
				while(I < S.Length && I - Start < 6 && Uri.IsHexDigit(S[I])){ I++; }
				var Hex = S.Substring(Start, I - Start);
				if(I < S.Length && (S[I] == ' ' || S[I] == '\t')){ I++; }
				if(int.TryParse(Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var Code)
					&& Code > 0 && Code <= 0x10FFFF){
					Sb.Append(char.ConvertFromUtf32(Code));
				}
				continue;
			}
			if(S[I] == '\n'){ I++; continue; }
			if(S[I] == '\r'){ I++; if(I < S.Length && S[I] == '\n'){ I++; } continue; }
			Sb.Append(S[I]);
			I++;
		}
		return Sb.ToString();
	}
}

static class CssInliner {
	static readonly Regex UrlRe = new(
		@"url\(\s*(?:""(?<q>[^""]*)""|'(?<s>[^']*)'|(?<u>[^)""'\s]*))\s*\)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	static readonly Regex ImportRe = new(
		@"@import\s+(?:url\(\s*(?:""(?<q>[^""]*)""|'(?<s>[^']*)'|(?<u>[^)""'\s]*))\s*\)|""(?<q2>[^""]*)""|'(?<s2>[^']*)')(?<media>[^;]*);",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public sealed class CssStat {
		public int Images;
		public int Css;
		public int Other;
		public int Unresolved;
	}

	/// 把 CSS 內的 url()/@import 換成 data URI，並把能取得的 @import 就地展開。
	public static string Inline(string Css, string? BaseUrl, ResourceIndex Res, CssStat Stat, List<string> Warnings, int Depth = 0){
		if(string.IsNullOrEmpty(Css)){ return Css; }

		var Text = Css;
		if(Depth == 0){ Text = StripCharset(Text); }
		if(Depth < 4){
			Text = ImportRe.Replace(Text, M => {
				var Ref = CssEscape.Unescape(FirstGroup(M, "q", "s", "u", "q2", "s2"));
				var Media = M.Groups["media"].Value.Trim();
				var Target = Res.Find(Ref, BaseUrl);
				if(Target == null){
					Stat.Unresolved++;
					return "";  // 取不到就丟掉這條 @import，避免輸出檔去連網
				}
				if(!Target.IsCss){
					Stat.Unresolved++;
					return "";
				}
				var Inner = Inline(Target.Text(), Target.BaseUrl ?? BaseUrl, Res, Stat, Warnings, Depth + 1);
				Stat.Css++;
				if(Media.Length == 0 || Media.Equals("all", StringComparison.OrdinalIgnoreCase)){
					return Inner;
				}
				return "@media " + Media + "{\n" + Inner + "\n}";
			});
		}

		Text = UrlRe.Replace(Text, M => {
			var Raw = M.Value;
			var Ref = CssEscape.Unescape(FirstGroup(M, "q", "s", "u"));
			var Data = Materialize(Ref, BaseUrl, Res, Stat, Warnings);
			if(Data == null){ return Raw; }
			return "url(\"" + Data + "\")";
		});
		return Text;
	}

	/// 回傳可直接放進 url() 的字串；null 表示保留原樣。
	static string? Materialize(string Ref, string? BaseUrl, ResourceIndex Res, CssStat Stat, List<string> Warnings){
		if(string.IsNullOrWhiteSpace(Ref)){ return null; }
		var R = Ref.Trim();
		if(R.StartsWith("data:", StringComparison.OrdinalIgnoreCase)){ return R; }
		if(R.StartsWith("#")){ return null; }
		var Target = Res.Find(R, BaseUrl);
		if(Target == null){
			Stat.Unresolved++;
			if(Warnings.Count < 6){ Warnings.Add("CSS 引用無法對應到資源: " + Trim(R)); }
			return null;
		}
		Count(Stat, Target.MediaType);
		return Target.DataUri;
	}

	/// 移除 @charset 規則：內容已經統一成 UTF-8，這條在 <style> 內既無效也多餘。
	public static string StripCharset(string Css){
		return CharsetRe.Replace(Css, "", 1);
	}

	static readonly Regex CharsetRe = new(@"^\s*@charset\s+[^;]+;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static void Count(CssStat Stat, string MediaType){
		if(MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)){
			Stat.Images++;
		} else if(MediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase)){
			Stat.Css++;
		} else {
			Stat.Other++;
		}
	}

	public static string FirstGroup(Match M, params string[] Names){
		foreach(var N in Names){
			var G = M.Groups[N];
			if(G.Success && G.Value.Length > 0){ return HtmlEntity.Decode(G.Value); }
		}
		return "";
	}

	static string Trim(string S) => S.Length <= 100 ? S : S.Substring(0, 100) + "…";
}

// ============================================================================
// HTML 改寫：把資源引用換成 data URI
// ============================================================================
sealed class HtmlInliner {
	readonly string Html;
	readonly string? BaseUrl;
	readonly ResourceIndex Res;
	readonly bool StripScripts;

	public int Images;
	public int CssCount;
	public int Other;
	public int Unresolved;
	public readonly List<string> Warnings = new();
	public readonly List<string> UnresolvedSamples = new();

	static readonly HashSet<string> HintRels = new(StringComparer.OrdinalIgnoreCase){
		"dns-prefetch", "preconnect", "prefetch", "preload", "prerender", "manifest", "search",
	};

	readonly CssInliner.CssStat CssStat = new();
	readonly int Depth;

	public HtmlInliner(string Html, string? BaseUrl, ResourceIndex Res, bool StripScripts, int Depth = 0){
		this.Html = Html;
		this.BaseUrl = BaseUrl;
		this.Res = Res;
		this.StripScripts = StripScripts;
		this.Depth = Depth;
	}

	/// 把子框架的統計併進來，讓主文件的統計涵蓋整棵框架樹。
	public void Absorb(HtmlInliner Sub){
		Images += Sub.Images;
		CssCount += Sub.CssCount;
		Other += Sub.Other;
		Unresolved += Sub.Unresolved;
		foreach(var W in Sub.Warnings){
			if(Warnings.Count < 6){ Warnings.Add(W); }
		}
		foreach(var S in Sub.UnresolvedSamples){
			if(UnresolvedSamples.Count < 8){ UnresolvedSamples.Add(S); }
		}
	}

	public string Run(){
		var Sb = new StringBuilder(Html.Length + Html.Length / 4);
		var I = 0;
		var N = Html.Length;
		while(I < N){
			var Lt = Html.IndexOf('<', I);
			if(Lt < 0){
				Sb.Append(Html, I, N - I);
				break;
			}
			if(Lt > I){ Sb.Append(Html, I, Lt - I); }

			if(At(Lt, "<!--")){
				var End = Html.IndexOf("-->", Lt + 4, StringComparison.Ordinal);
				if(End < 0){ Sb.Append(Html, Lt, N - Lt); break; }
				Sb.Append(Html, Lt, End + 3 - Lt);
				I = End + 3;
				continue;
			}
			if(At(Lt, "<![CDATA[")){
				var End = Html.IndexOf("]]>", Lt + 9, StringComparison.Ordinal);
				if(End < 0){ Sb.Append(Html, Lt, N - Lt); break; }
				Sb.Append(Html, Lt, End + 3 - Lt);
				I = End + 3;
				continue;
			}
			if(At(Lt, "<!") || At(Lt, "<?")){
				var End = Html.IndexOf('>', Lt);
				if(End < 0){ Sb.Append(Html, Lt, N - Lt); break; }
				Sb.Append(Html, Lt, End + 1 - Lt);
				I = End + 1;
				continue;
			}
			if(At(Lt, "</")){
				var End = Html.IndexOf('>', Lt);
				if(End < 0){ Sb.Append(Html, Lt, N - Lt); break; }
				Sb.Append(Html, Lt, End + 1 - Lt);
				I = End + 1;
				continue;
			}

			var Gt = HtmlToken.FindTagEnd(Html, Lt);
			if(Gt < 0){ Sb.Append(Html, Lt, N - Lt); break; }
			var Tag = HtmlToken.Parse(Html, Lt, Gt);

			switch(Tag.NameLower){
				case "style": {
					Sb.Append(Html, Lt, Gt + 1 - Lt);
					var Close = IndexOfIgnoreCase("</style", Gt + 1);
					var ContentEnd = Close < 0 ? N : Close;
					Sb.Append(CssInliner.Inline(Html.Substring(Gt + 1, ContentEnd - Gt - 1), BaseUrl, Res, CssStat, Warnings));
					I = ContentEnd;
					continue;
				}
				case "script": {
					var Close = IndexOfIgnoreCase("</script", Gt + 1);
					var ContentEnd = Close < 0 ? N : Close;
					if(StripScripts){
						var Tail = Html.IndexOf('>', ContentEnd);
						I = Tail < 0 ? N : Tail + 1;
						continue;
					}
					Sb.Append(Html, Lt, ContentEnd - Lt);
					I = ContentEnd;
					continue;
				}
				case "title":
				case "textarea":
				case "xmp":
				case "plaintext": {
					var Close = IndexOfIgnoreCase("</" + Tag.NameLower, Gt + 1);
					var ContentEnd = Close < 0 ? N : Close;
					Sb.Append(Html, Lt, ContentEnd - Lt);
					I = ContentEnd;
					continue;
				}
			}

			var Emitted = ProcessTag(Tag, Lt, Gt);
			if(Emitted.Length > 0){ Sb.Append(Emitted); }
			I = Gt + 1;
		}

		Images += CssStat.Images;
		CssCount += CssStat.Css;
		Other += CssStat.Other;
		Unresolved += CssStat.Unresolved;
		foreach(var S in Warnings){ if(UnresolvedSamples.Count < 8){ UnresolvedSamples.Add(S); } }
		return Sb.ToString();
	}

	string ProcessTag(HtmlTag Tag, int Lt, int Gt){
		// link：樣式表就地內嵌、圖示轉 data URI、純網路提示移除
		if(Tag.NameLower == "link"){
			var Rel = (Tag.Value("rel") ?? "").Trim().ToLowerInvariant();
			var Href = Tag.Value("href");
			var Rels = Rel.Split(new[]{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries);
			if(Rels.Contains("stylesheet")){
				var Target = Res.Find(Href, BaseUrl);
				if(Target != null && Target.IsCss){
					var Inner = CssInliner.Inline(Target.Text(), Target.BaseUrl ?? BaseUrl, Res, CssStat, Warnings);
					CssCount++;
					var Media = Tag.Value("media");
					var MediaAttr = string.IsNullOrWhiteSpace(Media) ? "" : " media=\"" + HtmlEntity.EncodeAttr(Media) + "\"";
					return "<style" + MediaAttr + ">" + Inner + "</style>";
				}
				// 本身就是 data: 的樣式表已經自帶內容，原樣保留即可
				if(Href != null && Href.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)){
					return Html.Substring(Lt, Gt + 1 - Lt);
				}
				NoteUnresolved(Href);
				return Html.Substring(Lt, Gt + 1 - Lt);
			}
			if(Rels.Any(R => R.Contains("icon"))){
				var Data = Materialize(Href);
				if(Data == null){ return ""; }
				return Rebuild(Tag, Lt, Gt, new Dictionary<string, string?>{ ["href"] = Data });
			}
			if(Rels.Any(R => HintRels.Contains(R))){
				return "";
			}
		}

		var Replaces = new Dictionary<string, string?>(StringComparer.Ordinal);
		var HasSrc = false;
		var SrcOk = false;

		foreach(var A in Tag.Attrs){
			switch(A.NameLower){
				case "style": {
					var New = CssInliner.Inline(A.Value ?? "", BaseUrl, Res, CssStat, Warnings);
					if(!string.Equals(New, A.Value, StringComparison.Ordinal)){ Replaces["style"] = New; }
					break;
				}
				case "src": {
					HasSrc = true;
					var Data = Tag.NameLower is "iframe" or "frame"
						? MaterializeFrame(A.Value)
						: Materialize(A.Value);
					if(Data != null){ Replaces["src"] = Data; SrcOk = true; }
					break;
				}
				case "srcset": {
					var New = RewriteSrcSet(A.Value);
					if(New != null){ Replaces["srcset"] = New; }
					break;
				}
				case "poster":
				case "data":
					if(Tag.NameLower is "video" or "object"){
						var Data = Materialize(A.Value);
						if(Data != null){ Replaces[A.NameLower] = Data; }
					}
					break;
				case "background": {
					var Data = Materialize(A.Value);
					if(Data != null){ Replaces["background"] = Data; }
					break;
				}
				case "data-src":
				case "data-original":
				case "data-actualsrc":
				case "data-lazy-src":
				case "data-lazyload":
					// 延遲載入屬性：值本身換成 data URI，順便記下來，必要時補到 src 上
					var LazyRes = Materialize(A.Value);
					if(LazyRes != null){
						Replaces[A.NameLower] = LazyRes;
						if(!Replaces.ContainsKey("__lazy")){ Replaces["__lazy"] = LazyRes; }
					}
					break;
				case "href":
					// 只有 SVG 的 image/use 才把 href 當資源
					if(Tag.NameLower is "image" or "use"){
						var Data = Materialize(A.Value);
						if(Data != null){ Replaces["href"] = Data; }
					}
					break;
				case "xlink:href":
					if(Tag.NameLower is "image" or "use"){
						var Data = Materialize(A.Value);
						if(Data != null){ Replaces["xlink:href"] = Data; }
					}
					break;
			}
		}

		// 延遲載入：src 取不到、但 lazy 屬性有資源時，用 lazy 的值補位
		if(Replaces.TryGetValue("__lazy", out var LazyData) && LazyData != null){
			Replaces.Remove("__lazy");
			if(!HasSrc || !SrcOk){
				Replaces["src"] = LazyData;
			}
		} else {
			Replaces.Remove("__lazy");
		}

		if(Replaces.Count == 0){ return Html.Substring(Lt, Gt + 1 - Lt); }
		return Rebuild(Tag, Lt, Gt, Replaces);
	}

	/// 用改過的屬性值重建標籤；未改動的屬性保留原文。
	string Rebuild(HtmlTag Tag, int Lt, int Gt, Dictionary<string, string?> Replaces){
		var Sb = new StringBuilder(Gt + 1 - Lt + 64);
		Sb.Append('<').Append(Tag.Name);
		var Used = new HashSet<string>(StringComparer.Ordinal);
		foreach(var A in Tag.Attrs){
			if(Replaces.TryGetValue(A.NameLower, out var NewValue)){
				Used.Add(A.NameLower);
				if(NewValue == null){ continue; }
				Sb.Append(' ').Append(A.Name).Append("=\"").Append(HtmlEntity.EncodeAttr(NewValue)).Append('"');
				continue;
			}
			Sb.Append(' ').Append(Html.Substring(A.RawStart, A.RawEnd - A.RawStart));
		}
		foreach(var Kv in Replaces){
			if(Kv.Key.StartsWith("__") || Used.Contains(Kv.Key)){ continue; }
			if(Kv.Value == null){ continue; }
			Sb.Append(' ').Append(Kv.Key).Append("=\"").Append(HtmlEntity.EncodeAttr(Kv.Value)).Append('"');
		}
		if(Tag.SelfClosing){ Sb.Append(" /"); }
		Sb.Append('>');
		return Sb.ToString();
	}

	string? RewriteSrcSet(string? Value){
		if(string.IsNullOrWhiteSpace(Value)){ return null; }
		var Parts = Value.Split(',');
		var Changed = false;
		var Out = new List<string>(Parts.Length);
		foreach(var PartRaw in Parts){
			var Part = PartRaw.Trim();
			if(Part.Length == 0){ continue; }
			var Space = Part.IndexOf(' ');
			var Url = Space < 0 ? Part : Part.Substring(0, Space);
			var Descriptor = Space < 0 ? "" : Part.Substring(Space);
			var Data = Materialize(Url);
			if(Data != null){
				Out.Add(Data + Descriptor);
				Changed = true;
			} else {
				Out.Add(Url.TrimEnd(',') + Descriptor);
			}
		}
		return Changed ? string.Join(", ", Out) : null;
	}

	string? Materialize(string? Ref){
		if(string.IsNullOrWhiteSpace(Ref)){ return null; }
		var R = Ref.Trim();
		if(R.StartsWith("data:", StringComparison.OrdinalIgnoreCase)){ return null; }
		if(R.StartsWith("#")){ return null; }
		var Target = Res.Find(R, BaseUrl);
		if(Target == null){
			NoteUnresolved(R);
			return null;
		}
		CssInliner.Count(CssStat, Target.MediaType);
		return Target.DataUri;
	}

	/// 子框架指向另一個 text/html 部分時，遞迴轉換後以 data:text/html 內嵌，
	/// 這樣框架頁面裡的圖片與樣式同樣不會留在外面。
	string? MaterializeFrame(string? Ref){
		if(string.IsNullOrWhiteSpace(Ref)){ return null; }
		var R = Ref.Trim();
		if(R.StartsWith("data:", StringComparison.OrdinalIgnoreCase)){ return null; }
		if(R.StartsWith("#")){ return null; }
		var Target = Res.Find(R, BaseUrl);
		if(Target == null){
			NoteUnresolved(R);
			return null;
		}
		if(!Target.IsHtml){
			CssInliner.Count(CssStat, Target.MediaType);
			return Target.DataUri;
		}
		if(Depth >= 2){ return null; }
		var Inner = TextCodec.Decode(Target.Bytes, Target.Charset);
		var Sub = new HtmlInliner(Inner, Target.BaseUrl ?? BaseUrl, Res, StripScripts, Depth + 1);
		var SubHtml = MhtConverter.EnsureUtf8Meta(Sub.Run());
		Absorb(Sub);
		Other++;
		return "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(new UTF8Encoding(false).GetBytes(SubHtml));
	}

	void NoteUnresolved(string? Ref){
		Unresolved++;
		if(UnresolvedSamples.Count < 8 && !string.IsNullOrWhiteSpace(Ref)){
			UnresolvedSamples.Add(Ref.Trim().Length > 110 ? Ref.Trim().Substring(0, 110) + "…" : Ref.Trim());
		}
	}

	int IndexOfIgnoreCase(string Needle, int From){
		return Html.IndexOf(Needle, From, StringComparison.OrdinalIgnoreCase);
	}

	bool At(int Pos, string Token){
		return string.CompareOrdinal(Html, Pos, Token, 0, Token.Length) == 0;
	}
}

// ============================================================================
// 轉換主體
// ============================================================================
sealed class MhtConvertResult {
	public string Html = "";
	public int ResourceCount;
	public int InlinedImages;
	public int InlinedCss;
	public int InlinedOther;
	public int Unresolved;
	public readonly List<string> UnresolvedSamples = new();
	public readonly List<string> Warnings = new();
}

static class MhtConverter {
	public static void Inspect(byte[] Raw, string Path){
		var Root = MimeParser.Parse(Raw);
		Echo("檔案: " + Path + "  " + Raw.Length + " bytes");
		var No = 0;
		Walk(Root, 0);
		void Walk(MimePart P, int Depth){
			No++;
			var Indent = new string(' ', Depth * 2);
			var Cid = P.ContentId ?? "";
			var Loc = P.ContentLocation ?? "";
			Echo($"{Indent}#{No,-3} {P.MediaType,-26} enc={P.TransferEncoding ?? "-",-16} "
				+ $"bytes~{P.BodyEnd - P.BodyStart,-8} cid={Short(Cid)} loc={Short(Loc)}");
			foreach(var C in P.Children){ Walk(C, Depth + 1); }
		}
		static string Short(string S) => S.Length <= 60 ? S : S.Substring(0, 57) + "…";
	}

	public static MhtConvertResult Convert(byte[] Raw, bool StripScripts){
		var Root = MimeParser.Parse(Raw);
		var RootHtml = FindRootHtml(Root) ?? throw new MhtFormatException("找不到 text/html 部分，這可能不是 MHTML 檔");
		var AllHtml = new List<MimePart>();
		CollectHtml(Root, AllHtml);

		var BaseUrl = FirstNonEmpty(
			NotCid(RootHtml.ContentLocation),
			Root.Headers.Get("Snapshot-Content-Location"),
			Root.Headers.Get("Content-Location"));

		// 建立資源索引（排除根 HTML 本身）
		var Index = new ResourceIndex();
		WalkParts(Root, P => {
			if(ReferenceEquals(P, RootHtml)){ return; }
			if(P.IsMultipart){ return; }
			var Bytes = P.Decoded(Raw);
			if(Bytes.Length == 0){ return; }
			var Res = new MhtRes {
				Bytes = Bytes,
				MediaType = NormalizeMediaType(P, Bytes),
				Charset = P.Charset,
				ContentLocation = P.ContentLocation,
				Cid = P.ContentId,
			};
			Res.BaseUrl = P.ContentLocation != null && !P.ContentLocation.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
				? ResourceIndex.ToAbsolute(P.ContentLocation, BaseUrl)
				: BaseUrl;
			Index.Add(Res);
		});

		var HtmlBytes = RootHtml.Decoded(Raw);
		var HtmlText = TextCodec.Decode(HtmlBytes, RootHtml.Charset);
		if(HtmlText.Length == 0){ throw new MhtFormatException("HTML 部分的內容是空的"); }

		var EffectiveBase = EffectiveBaseUrl(HtmlText, BaseUrl);
		var Inliner = new HtmlInliner(HtmlText, EffectiveBase, Index, StripScripts);
		var Output = Inliner.Run();
		Output = EnsureUtf8Meta(Output);

		var R = new MhtConvertResult {
			Html = Output,
			ResourceCount = Index.Count,
			InlinedImages = Inliner.Images,
			InlinedCss = Inliner.CssCount,
			InlinedOther = Inliner.Other,
			Unresolved = Inliner.Unresolved,
		};
		R.UnresolvedSamples.AddRange(Inliner.UnresolvedSamples);
		R.Warnings.AddRange(Inliner.Warnings);
		if(AllHtml.Count > 1){
			R.Warnings.Add($"檔案內有 {AllHtml.Count} 個 text/html 部分；子框架只有在被 iframe/frame 引用時才會內嵌");
		}
		if(Index.Count == 0){
			R.Warnings.Add("檔案內沒有可內嵌的外部資源，輸出可能仍引用遠端網址");
		}
		return R;
	}

	static void CollectHtml(MimePart P, List<MimePart> Out){
		if(P.IsHtml){ Out.Add(P); }
		foreach(var C in P.Children){ CollectHtml(C, Out); }
	}

	static void WalkParts(MimePart P, Action<MimePart> Visit){
		Visit(P);
		foreach(var C in P.Children){ WalkParts(C, Visit); }
	}

	/// 選出主文件：先看 multipart/related 的 start 參數，再退回第一個 text/html。
	static MimePart? FindRootHtml(MimePart Root){
		var All = new List<MimePart>();
		CollectHtml(Root, All);
		if(All.Count == 0){ return null; }
		if(All.Count == 1){ return All[0]; }
		var Start = Root.Params.TryGetValue("start", out var S) ? ResourceIndex.NormalizeCid(S) : null;
		if(!string.IsNullOrEmpty(Start)){
			var ByStart = All.FirstOrDefault(P =>
				P.ContentId != null && string.Equals(ResourceIndex.NormalizeCid(P.ContentId), Start, StringComparison.OrdinalIgnoreCase));
			if(ByStart != null){ return ByStart; }
		}
		// 優先選最上層的那個
		var Top = All.OrderBy(P => DepthOf(Root, P)).First();
		return Top;
	}

	static int DepthOf(MimePart Root, MimePart Target){
		var Depth = 0;
		var Found = false;
		void Dfs(MimePart P, int D){
			if(Found){ return; }
			if(ReferenceEquals(P, Target)){ Depth = D; Found = true; return; }
			foreach(var C in P.Children){ Dfs(C, D + 1); }
		}
		Dfs(Root, 0);
		return Found ? Depth : 99;
	}

	static string? NotCid(string? S){
		if(string.IsNullOrWhiteSpace(S)){ return null; }
		return S.Trim().StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? null : S.Trim();
	}

	static string? FirstNonEmpty(params string?[] Values){
		foreach(var V in Values){
			if(!string.IsNullOrWhiteSpace(V)){ return V.Trim(); }
		}
		return null;
	}

	/// Content-Type 缺失或過於籠統時，用副檔名與檔案頭猜一個像樣的媒體型別。
	static string NormalizeMediaType(MimePart P, byte[] Bytes){
		var Mt = (P.MediaType ?? "").Trim().ToLowerInvariant();
		if(Mt.StartsWith("multipart/")){ return "application/octet-stream"; }
		var IsGeneric = Mt.Length == 0
			|| Mt == "application/octet-stream"
			|| Mt == "binary/octet-stream"
			|| Mt == "application/unknown"
			|| Mt == "*/*"
			|| Mt == "text/plain";   // 沒寫 Content-Type 時 MIME 預設 text/plain，但內容可能是圖片
		if(!IsGeneric){
			var Semi = Mt.IndexOf(';');
			return Semi < 0 ? Mt : Mt.Substring(0, Semi).Trim();
		}
		// 二進位內容以檔案頭為準，其次才看網址副檔名
		var Guess = GuessFromContent(Bytes);
		if(Guess == null && IsGeneric && Mt == "text/plain"){
			return "text/plain";
		}
		Guess ??= GuessFromLocation(P.ContentLocation);
		return Guess ?? "application/octet-stream";
	}

	static string? GuessFromContent(byte[] B){
		if(B.Length >= 8 && B[0] == 0x89 && B[1] == 0x50 && B[2] == 0x4E && B[3] == 0x47){ return "image/png"; }
		if(B.Length >= 3 && B[0] == 0xFF && B[1] == 0xD8 && B[2] == 0xFF){ return "image/jpeg"; }
		if(B.Length >= 4 && B[0] == 'G' && B[1] == 'I' && B[2] == 'F' && B[3] == '8'){ return "image/gif"; }
		if(B.Length >= 12 && B[0] == 'R' && B[1] == 'I' && B[2] == 'F' && B[3] == 'F'
			&& B[8] == 'W' && B[9] == 'E' && B[10] == 'B' && B[11] == 'P'){ return "image/webp"; }
		if(B.Length >= 4 && B[0] == 'w' && B[1] == 'O' && B[2] == 'F' && B[3] == 'F'){ return "font/woff"; }
		if(B.Length >= 4 && B[0] == 'w' && B[1] == 'O' && B[2] == 'F' && B[3] == '2'){ return "font/woff2"; }
		if(B.Length >= 4 && B[0] == 0 && B[1] == 1 && B[2] == 0 && B[3] == 0){ return "font/ttf"; }
		if(B.Length >= 4 && B[0] == 'O' && B[1] == 'T' && B[2] == 'T' && B[3] == 'O'){ return "font/otf"; }
		return null;
	}

	static string? GuessFromLocation(string? Location){
		if(string.IsNullOrWhiteSpace(Location)){ return null; }
		var PathPart = Location;
		var Q = PathPart.IndexOf('?');
		if(Q >= 0){ PathPart = PathPart.Substring(0, Q); }
		var Dot = PathPart.LastIndexOf('.');
		if(Dot < 0){ return null; }
		var Ext = PathPart.Substring(Dot + 1).ToLowerInvariant();
		return Ext switch {
			"png" => "image/png",
			"jpg" or "jpeg" => "image/jpeg",
			"gif" => "image/gif",
			"webp" => "image/webp",
			"bmp" => "image/bmp",
			"ico" => "image/x-icon",
			"svg" => "image/svg+xml",
			"css" => "text/css",
			"js" => "text/javascript",
			"woff" => "font/woff",
			"woff2" => "font/woff2",
			"ttf" => "font/ttf",
			"otf" => "font/otf",
			"eot" => "application/vnd.ms-fontobject",
			_ => null,
		};
	}

	/// 輸出檔一律以 UTF-8 寫入；若原文宣告的編碼不是 UTF-8（或根本沒宣告），補一個 meta。
	/// 頁面若自帶 <base href>，相對引用要按它解析，否則會全部對不上。
	static readonly Regex BaseHrefRe = new(
		@"<base\b[^>]*?href\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static string? EffectiveBaseUrl(string Html, string? BaseUrl){
		var M = BaseHrefRe.Match(Html);
		if(!M.Success){ return BaseUrl; }
		var Raw = M.Groups[1].Value.Trim();
		if(Raw.Length >= 2 && ((Raw[0] == '"' && Raw[^1] == '"') || (Raw[0] == '\'' && Raw[^1] == '\''))){
			Raw = Raw.Substring(1, Raw.Length - 2);
		}
		Raw = HtmlEntity.Decode(Raw).Trim();
		if(Raw.Length == 0){ return BaseUrl; }
		var Abs = ResourceIndex.ToAbsolute(Raw, BaseUrl);
		return Abs.Length == 0 ? BaseUrl : Abs;
	}

	static readonly Regex MetaCharsetTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	static readonly Regex CharsetValueRe = new(
		@"(charset\s*=\s*[""']?\s*)([A-Za-z0-9_.:\-]+)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static string EnsureUtf8Meta(string Html){
		var AnyDecl = false;
		var AllUtf8 = true;
		var Rewritten = MetaCharsetTag.Replace(Html, M => {
			var TagText = M.Value;
			var VM = CharsetValueRe.Match(TagText);
			if(!VM.Success){ return TagText; }
			AnyDecl = true;
			if(TextCodec.NormalizeCharset(VM.Groups[2].Value) != "utf-8"){ AllUtf8 = false; }
			// 只換 charset 的值，保留引號與其餘屬性原樣
			return CharsetValueRe.Replace(TagText, "$1utf-8");
		});
		if(AnyDecl){
			return AllUtf8 ? Html : Rewritten;
		}
		var Insert = "<meta charset=\"utf-8\">";
		var HeadTag = Regex.Match(Html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
		if(HeadTag.Success){
			return Html.Substring(0, HeadTag.Index + HeadTag.Length) + Insert + Html.Substring(HeadTag.Index + HeadTag.Length);
		}
		var HtmlTag = Regex.Match(Html, @"<html\b[^>]*>", RegexOptions.IgnoreCase);
		if(HtmlTag.Success){
			return Html.Substring(0, HtmlTag.Index + HtmlTag.Length) + Insert + Html.Substring(HtmlTag.Index + HtmlTag.Length);
		}
		return Insert + Html;
	}
}
