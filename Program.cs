// =====================================================================================
//  PROGRAMA DE CONSOLA: REGISTRO E IDENTIFICACIÓN DE HUELLAS CON ZKTeco ZK9500
// -------------------------------------------------------------------------------------
//  Primera versión, sin web: un menú en la terminal para probar el lector.
//    1) Registrar  -> pide un nombre y 3 capturas del mismo dedo
//    2) Identificar -> pone el dedo y dice de quién es
//    3) Listar     -> muestra los registrados
//  Las plantillas se guardan en Base64 dentro de huellas.json para usarlas en otro sistema.
//  (La versión para sistemas web es el proyecto HuellasAgente.)
// =====================================================================================

using System.Text.Json;
using libzkfpcsharp; // Librería C# del ZKFinger SDK (lib\libzkfpcsharp.dll)

// ---- Carga de los usuarios guardados ----
const string DbFile = "huellas.json";
var opciones = new JsonSerializerOptions { WriteIndented = true };
var usuarios = File.Exists(DbFile)
    ? JsonSerializer.Deserialize<List<Usuario>>(File.ReadAllText(DbFile)) ?? new()
    : new List<Usuario>();

// ---- Inicio del SDK y apertura del lector ----
// Init: inicializa el SDK. GetDeviceCount: cuántos lectores hay conectados.
if (zkfp2.Init() != zkfperrdef.ZKFP_ERR_OK) { Console.WriteLine("No se pudo iniciar el SDK."); return; }
if (zkfp2.GetDeviceCount() == 0) { Console.WriteLine("No hay lector conectado."); zkfp2.Terminate(); return; }

IntPtr dev = zkfp2.OpenDevice(0); // abre el primer lector conectado
IntPtr db = zkfp2.DBInit();       // memoria del SDK para guardar y comparar huellas
if (dev == IntPtr.Zero || db == IntPtr.Zero) { Console.WriteLine("No se pudo abrir el lector."); zkfp2.Terminate(); return; }

// Parámetros del SDK: 1 = ancho y 2 = alto de la imagen de la huella (300 x 375 en el ZK9500)
int ancho = LeerParametro(dev, 1), alto = LeerParametro(dev, 2);
Console.WriteLine($"ZK9500 abierto. Imagen {ancho}x{alto}. Usuarios cargados: {usuarios.Count}");

// Carga las huellas guardadas en la base en memoria del SDK (para identificar 1:N)
foreach (var u in usuarios) zkfp2.DBAdd(db, u.Id, zkfp2.Base64ToBlob(u.Plantilla));

// ---- Menú principal ----
// "finally" garantiza que el lector y el SDK se liberen aunque el programa termine con error.
try
{
    while (true)
    {
        Console.WriteLine("\n1) Registrar huella  2) Identificar  3) Listar  0) Salir");
        Console.Write("> ");
        switch (Console.ReadLine()?.Trim())
        {
            case "1": Registrar(); break;
            case "2": Identificar(); break;
            case "3": usuarios.ForEach(u => Console.WriteLine($"  [{u.Id}] {u.Nombre}  ({u.Fecha:g})")); break;
            case "0": return;
        }
    }
}
finally
{
    zkfp2.CloseDevice(dev);
    zkfp2.DBFree(db);
    zkfp2.Terminate();
}

// REGISTRAR: 3 capturas del mismo dedo -> se combinan en una plantilla -> se guarda en huellas.json
void Registrar()
{
    Console.Write("Nombre: ");
    string nombre = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(nombre)) return;

    // El SDK necesita 3 capturas del mismo dedo para generar una plantilla de registro
    var capturas = new byte[3][];
    for (int i = 0; i < 3; i++)
    {
        Console.WriteLine($"Coloque el dedo ({i + 1}/3)...");
        var (plantilla, imagen) = Capturar();
        // DBMatch compara dos plantillas: 0 = no coinciden (dedo distinto)
        if (i > 0 && zkfp2.DBMatch(db, capturas[0], plantilla) <= 0)
        {
            Console.WriteLine("No es el mismo dedo. Registro cancelado.");
            return;
        }
        capturas[i] = plantilla;
        if (i == 0) GuardarBmp($"huella_{nombre}.bmp", imagen, ancho, alto);
        if (i < 2) EsperarQueRetire();
    }

    // Evita duplicados: ¿esta huella ya está registrada?
    int idExistente = 0, score = 0;
    if (zkfp2.DBIdentify(db, capturas[0], ref idExistente, ref score) == zkfperrdef.ZKFP_ERR_OK)
    {
        Console.WriteLine($"Esa huella ya pertenece a [{idExistente}]. Registro cancelado.");
        return;
    }

    // DBMerge une las 3 capturas en una plantilla de registro más confiable
    byte[] registro = new byte[2048];
    int len = registro.Length;
    if (zkfp2.DBMerge(db, capturas[0], capturas[1], capturas[2], registro, ref len) != zkfperrdef.ZKFP_ERR_OK)
    {
        Console.WriteLine("No se pudo combinar las capturas. Intente de nuevo.");
        return;
    }
    registro = registro[..len];

    // Nuevo ID = el mayor existente + 1. Se agrega a la memoria del SDK y al archivo JSON.
    int id = usuarios.Count == 0 ? 1 : usuarios.Max(u => u.Id) + 1;
    zkfp2.DBAdd(db, id, registro);
    usuarios.Add(new Usuario(id, nombre, zkfp2.BlobToBase64(registro, len), DateTime.Now));
    File.WriteAllText(DbFile, JsonSerializer.Serialize(usuarios, opciones));
    Console.WriteLine($"Registrado [{id}] {nombre}. Guardado en {DbFile}");
}

// IDENTIFICAR: DBIdentify busca la huella entre todas las cargadas con DBAdd (búsqueda 1:N)
void Identificar()
{
    Console.WriteLine("Coloque el dedo para identificar...");
    var (plantilla, _) = Capturar();
    int id = 0, score = 0;
    if (zkfp2.DBIdentify(db, plantilla, ref id, ref score) == zkfperrdef.ZKFP_ERR_OK)
        Console.WriteLine($"Identificado: [{id}] {usuarios.FirstOrDefault(u => u.Id == id)?.Nombre}  (score {score})");
    else
        Console.WriteLine("Huella no reconocida.");
}

// Espera hasta que pongan un dedo. AcquireFingerprint no espera por sí solo:
// devuelve OK solo si en ese instante hay un dedo, por eso se repite cada 100 ms.
(byte[] plantilla, byte[] imagen) Capturar()
{
    byte[] imagen = new byte[ancho * alto];
    byte[] plantilla = new byte[2048];
    while (true)
    {
        int len = plantilla.Length;
        if (zkfp2.AcquireFingerprint(dev, imagen, plantilla, ref len) == zkfperrdef.ZKFP_ERR_OK)
            return (plantilla[..len], imagen);
        Thread.Sleep(100);
    }
}

void EsperarQueRetire()
{
    Console.WriteLine("Retire el dedo...");
    Thread.Sleep(1200);
}

// Lee un parámetro numérico del lector (el SDK lo entrega como 4 bytes)
static int LeerParametro(IntPtr dev, int codigo)
{
    byte[] buf = new byte[4];
    int size = 4, valor = 0;
    zkfp2.GetParameters(dev, codigo, buf, ref size);
    zkfp2.ByteArray2Int(buf, ref valor);
    return valor;
}

// Guarda la imagen cruda (escala de grises 8 bits) como BMP
static void GuardarBmp(string ruta, byte[] px, int w, int h)
{
    int fila = (w + 3) & ~3;
    int offset = 14 + 40 + 256 * 4;
    using var bw = new BinaryWriter(File.Create(ruta));
    bw.Write((ushort)0x4D42); bw.Write(offset + fila * h); bw.Write(0); bw.Write(offset);
    bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)8);
    bw.Write(0); bw.Write(fila * h); bw.Write(0); bw.Write(0); bw.Write(256); bw.Write(0);
    for (int i = 0; i < 256; i++) { bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)0); }
    var pad = new byte[fila - w];
    for (int y = h - 1; y >= 0; y--) { bw.Write(px, y * w, w); bw.Write(pad); }
}

// Una persona registrada, tal como se guarda en huellas.json (Plantilla en Base64)
record Usuario(int Id, string Nombre, string Plantilla, DateTime Fecha);
