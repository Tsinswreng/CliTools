# 生成 MHT 邊界樣例，供 Mht2Html.csx 的完備性測試用。
import base64, os, quopri, sys

DIR = r"E:\_code\CliTools\Csx\_MhtTest\synthetic"
os.makedirs(DIR, exist_ok=True)

PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
)
PNG64 = base64.b64encode(PNG).decode()

def w(name, data):
    if isinstance(data, str):
        data = data.encode("utf-8")
    p = os.path.join(DIR, name)
    open(p, "wb").write(data)
    print("wrote", name, len(data))

def b64(text):
    return base64.b64encode(text.encode("utf-8")).decode()

# ---------- A：base64 的 html 部分 + 純 LF 換行 + 巢狀 multipart/alternative ----------
htmlA = ("<!DOCTYPE html><html><head><title>A</title></head><body>"
         "<p>Alpha 甲</p><img src=\"http://example.com/img/a.png\">"
         "</body></html>")
A = "\n".join([
    "From: <Saved by Test>",
    "MIME-Version: 1.0",
    'Content-Type: multipart/related; boundary="BOUND-A"',
    "",
    "--BOUND-A",
    'Content-Type: multipart/alternative; boundary="INNER-A"',
    "",
    "--INNER-A",
    "Content-Type: text/plain",
    "Content-Location: http://example.com/a",
    "",
    "Alpha",
    "--INNER-A",
    "Content-Type: text/html",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/a",
    "",
    b64(htmlA),
    "--INNER-A--",
    "--BOUND-A",
    "Content-Type: image/png",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/a.png",
    "",
    PNG64,
    "--BOUND-A--",
    "",
])
w("A_nested_base64_lf.mht", A)

# ---------- B：GBK 頁面（charset=gb2312，quoted-printable）----------
htmlB_text = "<!DOCTYPE html><html><head><meta charset=\"gb2312\"><title>乙</title></head><body><p>中文測試：甲乙丙丁</p></body></html>"
qpB = quopri.encodestring(htmlB_text.encode("gbk")).decode("ascii").replace("\n", "\r\n")
B = "\r\n".join([
    "MIME-Version: 1.0",
    'Content-Type: multipart/related; boundary="BOUND-B"',
    "",
    "--BOUND-B",
    'Content-Type: text/html; charset="gb2312"',
    "Content-Transfer-Encoding: quoted-printable",
    "Content-Location: http://example.com/b",
    "",
    qpB,
    "--BOUND-B--",
    "",
])
w("B_gbk.mht", B)

# ---------- C：單一 text/html，沒有 multipart ----------
C = "\r\n".join([
    "Content-Type: text/html; charset=utf-8",
    "Content-Location: http://example.com/c",
    "Content-Transfer-Encoding: quoted-printable",
    "",
    "<!DOCTYPE html><html><head><title>C</title></head><body><p>Char 丙</p></body></html>",
    "",
])
w("C_plain_single.mht", C)

# ---------- D：iframe 指向子框架 html 部分 ----------
frameHtml = "<!DOCTYPE html><html><head><title>F</title></head><body><img src=\"http://example.com/img/f.png\"></body></html>"
parentHtml = ("<!DOCTYPE html><html><head><title>D</title></head><body>"
              "<iframe src=\"http://example.com/frame.html\" width=\"300\"></iframe>"
              "</body></html>")
D = "\r\n".join([
    "MIME-Version: 1.0",
    'Content-Type: multipart/related; type="text/html"; boundary="BOUND-D"',
    "",
    "--BOUND-D",
    "Content-Type: text/html",
    "Content-Transfer-Encoding: quoted-printable",
    "Content-Location: http://example.com/d",
    "",
    quopri.encodestring(parentHtml.encode()).decode().replace("\n", "\r\n"),
    "--BOUND-D",
    "Content-Type: text/html",
    "Content-Transfer-Encoding: quoted-printable",
    "Content-Location: http://example.com/frame.html",
    "",
    quopri.encodestring(frameHtml.encode()).decode().replace("\n", "\r\n"),
    "--BOUND-D",
    "Content-Type: image/png",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/f.png",
    "",
    PNG64,
    "--BOUND-D--",
    "",
])
w("D_iframe.mht", D)

# ---------- E：srcset / style 屬性 / @import / CSS 轉義 / data-src / 缺 Content-Type ----------
cssMain = ("@charset \"utf-8\";\n@import url(\"http://example.com/imported.css\");\n"
           ".a{background:url(http://example.com/img/a.png)}\n"
           ".b{background:url(https\\:\\/\\/example\\.com\\/img\\/escaped\\.png)}\n")
cssImported = ".imported{background:url('http://example.com/img/a.png')}\n"
htmlE = ("<!DOCTYPE html><html><head><title>E</title>"
         "<link rel=\"stylesheet\" href=\"http://example.com/main.css\">"
         "<link rel=\"preconnect\" href=\"https://cdn.example.com\">"
         "</head><body>"
         "<img src=\"http://example.com/img/a.png\" srcset=\"http://example.com/img/a.png 1x, http://example.com/img/b.png 2x\">"
         "<img data-src=\"http://example.com/img/b.png\">"
         "<div style=\"background-image:url(http://example.com/img/a.png)\">x</div>"
         "</body></html>")
E = "\r\n".join([
    "MIME-Version: 1.0",
    'Content-Type: multipart/related; type="text/html"; boundary="BOUND-E"',
    "",
    "--BOUND-E",
    "Content-Type: text/html",
    "Content-Transfer-Encoding: quoted-printable",
    "Content-Location: http://example.com/e",
    "",
    quopri.encodestring(htmlE.encode()).decode().replace("\n", "\r\n"),
    "--BOUND-E",
    "Content-Type: text/css",
    "Content-Transfer-Encoding: quoted-printable",
    "Content-Location: http://example.com/main.css",
    "",
    quopri.encodestring(cssMain.encode()).decode().replace("\n", "\r\n"),
    "--BOUND-E",
    "Content-Type: text/css",
    "Content-Location: http://example.com/imported.css",
    "",
    cssImported,
    "--BOUND-E",
    # 故意不寫 Content-Type，靠檔案頭猜
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/a.png",
    "",
    PNG64,
    "--BOUND-E",
    "Content-Type: image/png",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/b.png",
    "",
    PNG64,
    "--BOUND-E",
    "Content-Type: image/png",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/escaped.png",
    "",
    PNG64,
    "--BOUND-E--",
    "",
])
w("E_features.mht", E)

# ---------- F1：缺少結尾分隔線（截斷）----------
F = "\r\n".join([
    "MIME-Version: 1.0",
    'Content-Type: multipart/related; boundary="BOUND-F"',
    "",
    "--BOUND-F",
    "Content-Type: text/html",
    "Content-Location: http://example.com/f",
    "",
    "<html><body>truncated</body></html>",
    "--BOUND-F",
    "Content-Type: image/png",
    "Content-Transfer-Encoding: base64",
    "Content-Location: http://example.com/img/a.png",
    "",
    PNG64,
])
w("F_truncated.mht", F)

# ---------- F2：根本不是 MHTML ----------
w("F_notmht.txt", "hello, this is not mhtml at all\n")
print("done")
