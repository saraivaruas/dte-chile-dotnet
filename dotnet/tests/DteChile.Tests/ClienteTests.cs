using System.Text.Json;
using System.Xml;
using DteChile;

namespace DteChile.Tests;

public class ClienteTests
{
    private static (SiiClient Cliente, List<SiiRequest> Log) Falso(SiiAmbiente ambiente, params (int, string)[] respuestas)
    {
        var log = new List<SiiRequest>();
        var cola = new Queue<(int, string)>(respuestas);
        var cliente = new SiiClient(ambiente, null, (req, _) =>
        {
            log.Add(req);
            var (status, body) = cola.Count > 0 ? cola.Dequeue() : (500, "sin respuesta preparada");

            return Task.FromResult(new SiiResponse(status, body));
        });

        return (cliente, log);
    }

    private static (SiiClient, List<SiiRequest>) Falso(params (int, string)[] r) => Falso(SiiAmbiente.Certificacion, r);

    private static string SoapRetorno(string nombre, string interior)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body><{nombre} xmlns=\"http://DefaultNamespace\"><{nombre}Return>"
           + System.Security.SecurityElement.Escape(interior) + $"</{nombre}Return></{nombre}></soapenv:Body></soapenv:Envelope>";

    private static string RespSii(string hdr, string body = "")
        => "<SII:RESPUESTA xmlns:SII=\"http://www.sii.cl/XMLSchema\"><SII:RESP_HDR>" + hdr + "</SII:RESP_HDR>" + (body.Length > 0 ? "<SII:RESP_BODY>" + body + "</SII:RESP_BODY>" : string.Empty) + "</SII:RESPUESTA>";

    private static XmlDocument Envio(Certificate cert)
    {
        var (dte, _, _) = Pipeline.Dte(Fixtures.Factura(), cert: cert);

        return new EnvioDteBuilder().Build([dte], B.Carat, cert);
    }

    [Fact]
    public async Task Semilla_y_token_por_SOAP_hacia_el_ambiente_correcto()
    {
        var cert = Fixtures.Cert();
        var (c, log) = Falso((200, SoapRetorno("getSeed", RespSii("<ESTADO>00</ESTADO>", "<SEMILLA>123456789</SEMILLA>"))), (200, SoapRetorno("getToken", RespSii("<ESTADO>00</ESTADO>", "<TOKEN>ABCDEF123</TOKEN>"))));
        Assert.Equal("ABCDEF123", await c.AuthenticateAsync(cert));
        Assert.Equal("https://maullin.sii.cl/DTEWS/CrSeed.jws", log[0].Url);
        Assert.Equal("https://maullin.sii.cl/DTEWS/GetTokenFromSeed.jws", log[1].Url);
        Assert.Contains("&lt;Semilla&gt;123456789&lt;/Semilla&gt;", log[1].Body);
        Assert.Contains("SignatureValue", log[1].Body);
        var (p, logp) = Falso(SiiAmbiente.Produccion, (200, SoapRetorno("getSeed", RespSii("<ESTADO>00</ESTADO>", "<SEMILLA>1</SEMILLA>"))));
        await p.GetSeedAsync();
        Assert.Equal("https://palena.sii.cl/DTEWS/CrSeed.jws", logp[0].Url);
    }

    [Fact]
    public async Task Un_ESTADO_distinto_de_00_o_un_fallo_SOAP_dan_el_motivo_del_SII()
    {
        var (c1, _) = Falso((200, SoapRetorno("getSeed", RespSii("<ESTADO>-3</ESTADO><GLOSA>Error interno</GLOSA>"))));
        Assert.Contains("Error interno", (await Assert.ThrowsAsync<InvalidOperationException>(() => c1.GetSeedAsync())).Message);
        var (c2, _) = Falso((200, "<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><e:Body><e:Fault><faultstring>No autorizado</faultstring></e:Fault></e:Body></e:Envelope>"));
        Assert.Contains("No autorizado", (await Assert.ThrowsAsync<InvalidOperationException>(() => c2.GetSeedAsync())).Message);
        var (c3, _) = Falso((503, "<html>Servicio no disponible</html>"));
        Assert.Contains("HTTP 503", (await Assert.ThrowsAsync<InvalidOperationException>(() => c3.GetSeedAsync())).Message);
    }

    [Fact]
    public async Task Sube_un_EnvioDTE_con_los_campos_y_la_cookie_que_pide_el_SII_y_entrega_el_trackId()
    {
        var cert = Fixtures.Cert();
        var envio = Envio(cert);
        var (c, log) = Falso((200, "<RECEPCIONDTE><STATUS>0</STATUS><TRACKID>987654</TRACKID></RECEPCIONDTE>"));
        Assert.Equal(("987654", "0"), await c.UploadEnvioDteAsync(envio, "TKN", "11.111.111-1", "76.123.456-0"));
        var r = log[0];
        Assert.Equal("https://maullin.sii.cl/cgi_dte/UPL/DTEUpload", r.Url);
        Assert.Equal(["11111111", "1", "76123456", "0"], r.Form!.Take(4).Select(f => f.Content).ToArray());
        Assert.Contains("Cookie: TOKEN=TKN", r.Headers);
        Assert.Contains("<EnvioDTE", r.Form![4].Content);
        var (c2, _) = Falso((200, "<RECEPCIONDTE><STATUS>7</STATUS></RECEPCIONDTE>"));
        Assert.Contains("Esquema XML inválido", (await Assert.ThrowsAsync<InvalidOperationException>(() => c2.UploadEnvioDteAsync(envio, "TKN", "11111111-1", "76123456-0"))).Message);
    }

    [Fact]
    public async Task Consulta_el_estado_de_un_envio_y_lo_traduce()
    {
        var (c, log) = Falso((200, SoapRetorno("getEstUp", RespSii("<TRACKID>987654</TRACKID><ESTADO>EPR</ESTADO><GLOSA>Envio Procesado</GLOSA>", "<ACEPTADOS>2</ACEPTADOS><RECHAZADOS>1</RECHAZADOS><REPAROS>0</REPAROS>"))));
        var e = await c.GetUploadStatusAsync("76.123.456-0", "987654", "TKN");
        Assert.Equal(("EPR", "Envío procesado.", 2, 1, 0, "987654"), (e.Estado, e.Descripcion, e.Aceptados, e.Rechazados, e.Reparos, e.TrackId));
        Assert.Contains(">76123456</RutCompania>", log[0].Body);
        Assert.Contains("987654</TrackId>", log[0].Body);
        Assert.Contains("TKN</Token>", log[0].Body);
        var (c2, _) = Falso((200, SoapRetorno("getEstUp", RespSii("<ESTADO>ZZZ</ESTADO><GLOSA>Estado nuevo del SII</GLOSA>"))));
        Assert.Equal("Estado nuevo del SII", (await c2.GetUploadStatusAsync("76123456-0", "1", "T")).Descripcion);
    }

    [Fact]
    public async Task Consulta_el_estado_de_un_documento_con_la_fecha_en_el_formato_del_SII()
    {
        var (c, log) = Falso((200, SoapRetorno("getEstDte", RespSii("<ESTADO>DOK</ESTADO><GLOSA>Documento Recibido por el SII. Datos Coinciden con los Registrados</GLOSA>"))));
        var e = await c.GetDteStatusAsync("11111111-1", "76123456-0", "77111222-6", 33, 7, "2026-09-08", 297500, "TKN");
        Assert.Equal("DOK", e.Estado);
        foreach (var trozo in new[] { ">08092026</", ">33</", ">7</", ">297500</", ">77111222</", ">6</" })
        {
            Assert.Contains(trozo, log[0].Body);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => c.GetDteStatusAsync("11111111-1", "76123456-0", "77111222-6", 33, 7, "08-09-2026", 1, "T"));
    }

    [Fact]
    public async Task Boletas_por_la_API_REST_token_envio_y_estado()
    {
        var cert = Fixtures.Cert();
        var (dte, _, _) = B.BoletaFirmada(B.Boleta());
        var envio = new EnvioDteBuilder().BuildBoleta([dte], B.Carat, cert);
        var (c, log) = Falso(
            (200, RespSii("<ESTADO>00</ESTADO>", "<SEMILLA>555</SEMILLA>")),
            (200, RespSii("<ESTADO>00</ESTADO>", "<TOKEN>BOLTKN</TOKEN>")),
            (200, "{\"rut_emisor\":\"76123456-0\",\"rut_envia\":\"11111111-1\",\"trackid\":424242,\"estado\":\"REC\"}"),
            (200, "{\"trackid\":424242,\"estado\":\"EPR\",\"estadistica\":[{\"tipo\":39,\"informados\":1,\"aceptados\":1}]}"));
        var token = await c.AuthenticateBoletaAsync(cert);
        Assert.Equal("BOLTKN", token);
        Assert.Equal(("GET", "https://apicert.sii.cl/recursos/v1/boleta.electronica.semilla"), (log[0].Method, log[0].Url));
        Assert.Equal(("POST", "https://apicert.sii.cl/recursos/v1/boleta.electronica.token"), (log[1].Method, log[1].Url));
        Assert.Contains("<Semilla>555</Semilla>", log[1].Body);
        var r = await c.UploadBoletaAsync(envio, token, "11111111-1", "76123456-0");
        Assert.Equal(424242, r.GetProperty("trackid").GetInt32());
        Assert.Equal("https://pangal.sii.cl/recursos/v1/boleta.electronica.envio", log[2].Url);
        var e = await c.GetBoletaUploadStatusAsync("76.123.456-0", "424242", token);
        Assert.Equal("EPR", e.GetProperty("estado").GetString());
        Assert.Equal("https://apicert.sii.cl/recursos/v1/boleta.electronica.envio/76123456-0-424242", log[3].Url);
        var (p, logp) = Falso(SiiAmbiente.Produccion, (200, "{\"trackid\":1}"));
        await p.UploadBoletaAsync(envio, "T", "11111111-1", "76123456-0");
        Assert.Equal("https://rahue.sii.cl/recursos/v1/boleta.electronica.envio", logp[0].Url);
        var (m, _) = Falso((200, "No es JSON"));
        Assert.Contains("inesperado", (await Assert.ThrowsAsync<InvalidOperationException>(() => m.UploadBoletaAsync(envio, "T", "11111111-1", "76123456-0"))).Message);
    }
}
