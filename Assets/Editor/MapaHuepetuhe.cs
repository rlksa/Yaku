using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MapaHuepetuhe : EditorWindow
{
    // ---------- Medidas del mapa ----------
    const float TAM = 400f;
    const float ALTO = 40f;
    const int RES = 513;
    const float BASE_H = 7f;
    const float PLATAFORMA_H = 8.5f;
    const float RIO_FONDO = 2.5f;
    const float AGUA_H = 3.6f;
    const float ORILLA = 14f;
    const float SEMILLA = 137.5f;
    const float PUENTE_H = 7.6f;

    // ---------- Zonas (x, z) ----------
    static readonly Vector2 COM = new Vector2(130, 290);   // comunidad
    static readonly Vector2 MER = new Vector2(220, 285);   // mercado
    static readonly Vector2 SAL = new Vector2(175, 345);   // puesto de salud
    static readonly Vector2 MIN = new Vector2(290, 105);   // zona minera
    static readonly Vector2 PTE_C = new Vector2(200f, RioZ(200f));
    static readonly Vector2 PTE_N = NormalRio(200f);
    static readonly Vector2 PUENTE_N = PTE_C + PTE_N * 45f;
    static readonly Vector2 PUENTE_S = PTE_C - PTE_N * 45f;
    static readonly Vector3[] POZAS = { new Vector3(268, 90, 14), new Vector3(318, 85, 11) };

    static readonly Vector2[][] CAMINOS = {
        new[] { COM, MER },
        new[] { COM, SAL },
        new[] { COM, PUENTE_N },
        new[] { PUENTE_N, PUENTE_S },
        new[] { PUENTE_S, MIN },
    };

    enum Tipo { Arbol, Suave, Roca }
    enum Interior { Vivienda, Salud, Caseta }

    // ---------- Ajustes ----------
    public GameObject familia, doctor, vendedor, minero;
    float anchoRio = 8f;
    int arboles = 220;
    int pasto = 250;
    float escalaNat = 1f;

    // ---------- Estado de la generación ----------
    Terrain ter;
    System.Random rnd;
    Dictionary<string, Material> mats = new Dictionary<string, Material>();
    Material mMadera, mMaderaOsc, mGris, mOxido, mVerde, mBlanco, mCemento, mRojo, mAmarillo, mNegro, mPiel;
    Material[] paredes, techos;

    [MenuItem("Huepetuhe/Generar Mapa Completo")]
    static void Abrir() { GetWindow<MapaHuepetuhe>("Mapa Huepetuhe"); }

    void OnGUI()
    {
        GUILayout.Label("Ajustes del mapa", EditorStyles.boldLabel);
        anchoRio = EditorGUILayout.Slider("Ancho del río (m)", anchoRio, 4f, 20f);
        arboles = EditorGUILayout.IntSlider("Árboles", arboles, 0, 500);
        pasto = EditorGUILayout.IntSlider("Pasto y plantas", pasto, 0, 600);
        escalaNat = EditorGUILayout.Slider("Tamaño de árboles y rocas", escalaNat, 0.5f, 3f);

        GUILayout.Space(8);
        GUILayout.Label("Personajes propios (opcional, si no, usa muñecos simples)", EditorStyles.boldLabel);
        familia = (GameObject)EditorGUILayout.ObjectField("Familia", familia, typeof(GameObject), false);
        doctor = (GameObject)EditorGUILayout.ObjectField("Doctor / Enfermera", doctor, typeof(GameObject), false);
        vendedor = (GameObject)EditorGUILayout.ObjectField("Vendedor", vendedor, typeof(GameObject), false);
        minero = (GameObject)EditorGUILayout.ObjectField("Minero", minero, typeof(GameObject), false);

        GUILayout.Space(8);
        EditorGUILayout.HelpBox("Hazlo en una escena nueva y vacía (File > New Scene > Basic URP). Luego guarda con File > Save As.", MessageType.Info);
        if (GUILayout.Button("GENERAR MAPA COMPLETO", GUILayout.Height(34))) Generar();
    }

    // ================= Utilidades matemáticas =================

    static float S(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    static float RioZ(float x) { return 200f + 25f * Mathf.Sin(x / 60f); }

    static Vector2 NormalRio(float x)
    {
        float s = 25f / 60f * Mathf.Cos(x / 60f);
        return new Vector2(-s, 1f).normalized;
    }

    static float DistSeg(float px, float pz, Vector2 a, Vector2 b)
    {
        Vector2 p = new Vector2(px, pz);
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    static float DistCaminos(float x, float z)
    {
        float m = 1e9f;
        foreach (var c in CAMINOS) m = Mathf.Min(m, DistSeg(x, z, c[0], c[1]));
        return m;
    }

    static float Dist(float x, float z, Vector2 c) { return Vector2.Distance(new Vector2(x, z), c); }

    static bool EnPozo(float x, float z, float margen)
    {
        foreach (var p in POZAS)
            if (Vector2.Distance(new Vector2(x, z), new Vector2(p.x, p.y)) < p.z + margen) return true;
        return false;
    }

    float Suelo(float x, float z)
    {
        return ter.SampleHeight(new Vector3(x, 0f, z)) + ter.transform.position.y;
    }

    Vector3 P(float x, float z) { return new Vector3(x, Suelo(x, z), z); }

    // ================= Carpetas, materiales, prefabs =================

    static void Carpeta(string ruta)
    {
        if (AssetDatabase.IsValidFolder(ruta)) return;
        string padre = Path.GetDirectoryName(ruta).Replace("\\", "/");
        Carpeta(padre);
        AssetDatabase.CreateFolder(padre, Path.GetFileName(ruta));
    }

    Material Mat(string nombre, float r, float g, float b, float suave = 0.15f)
    {
        Material m;
        if (mats.TryGetValue(nombre, out m)) return m;
        Carpeta("Assets/Mapa/Materiales");
        string ruta = "Assets/Mapa/Materiales/Mat_" + nombre + ".mat";
        m = AssetDatabase.LoadAssetAtPath<Material>(ruta);
        if (m == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            m = new Material(sh);
            AssetDatabase.CreateAsset(m, ruta);
        }
        var c = new Color(r, g, b, 1f);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", suave);
        EditorUtility.SetDirty(m);
        mats[nombre] = m;
        return m;
    }

    void Paleta()
    {
        mMadera = Mat("Madera", 0.62f, 0.44f, 0.26f);
        mMaderaOsc = Mat("MaderaOscura", 0.38f, 0.26f, 0.16f);
        mGris = Mat("CalaminaGris", 0.55f, 0.57f, 0.6f, 0.35f);
        mOxido = Mat("CalaminaOxido", 0.55f, 0.3f, 0.2f, 0.25f);
        mVerde = Mat("CalaminaVerde", 0.2f, 0.4f, 0.3f, 0.3f);
        mBlanco = Mat("Blanco", 0.93f, 0.93f, 0.9f);
        mCemento = Mat("Cemento", 0.6f, 0.6f, 0.58f, 0.05f);
        mRojo = Mat("Rojo", 0.78f, 0.12f, 0.12f);
        mAmarillo = Mat("Amarillo", 0.95f, 0.8f, 0.1f);
        mNegro = Mat("Negro", 0.08f, 0.08f, 0.08f);
        mPiel = Mat("Piel", 0.8f, 0.6f, 0.45f);
        paredes = new[] {
            Mat("Pared_Madera", 0.72f, 0.52f, 0.32f),
            Mat("Pared_Celeste", 0.55f, 0.72f, 0.8f),
            Mat("Pared_Verde", 0.5f, 0.68f, 0.5f),
            Mat("Pared_Crema", 0.87f, 0.8f, 0.62f),
        };
        techos = new[] { mGris, mOxido, mVerde };
    }

    static TerrainLayer BuscarCapa(string nombre)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:TerrainLayer"))
        {
            string ruta = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(ruta) == nombre)
                return AssetDatabase.LoadAssetAtPath<TerrainLayer>(ruta);
        }
        return null;
    }

    List<GameObject> Cargar(string prefijo)
    {
        var lista = new List<GameObject>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            string ruta = AssetDatabase.GUIDToAssetPath(guid);
            if (!ruta.StartsWith("Assets/")) continue;
            if (!Path.GetFileName(ruta).StartsWith(prefijo)) continue;
            var pf = AssetDatabase.LoadAssetAtPath<GameObject>(ruta);
            if (pf != null) lista.Add(pf);
        }
        if (lista.Count == 0) Debug.LogWarning("No encontré prefabs que empiecen con: " + prefijo);
        return lista;
    }

    // ================= Piezas básicas =================

    GameObject Caja(Transform padre, string nombre, Vector3 pos, Vector3 tam, Material m, Vector3 rot = default(Vector3))
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = nombre;
        g.transform.SetParent(padre, false);
        g.transform.localPosition = pos;
        g.transform.localEulerAngles = rot;
        g.transform.localScale = tam;
        g.GetComponent<MeshRenderer>().sharedMaterial = m;
        return g;
    }

    GameObject Cil(Transform padre, string nombre, Vector3 pos, float diam, float alto, Material m, Vector3 rot = default(Vector3))
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        g.name = nombre;
        g.transform.SetParent(padre, false);
        g.transform.localPosition = pos;
        g.transform.localEulerAngles = rot;
        g.transform.localScale = new Vector3(diam, alto / 2f, diam);
        g.GetComponent<MeshRenderer>().sharedMaterial = m;
        return g;
    }

    Transform Raiz(Transform padre, string nombre, Vector3 pos, float rotY)
    {
        var r = new GameObject(nombre);
        r.transform.SetParent(padre, false);
        r.transform.position = pos;
        r.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        return r.transform;
    }

    void Segmento(Transform g, Vector3 centro, bool alLargoX, float u0, float u1, float y0, float y1, float e, Material m)
    {
        float len = u1 - u0, alto = y1 - y0;
        if (len < 0.01f || alto < 0.01f) return;
        float u = (u0 + u1) / 2f;
        float y = centro.y + (y0 + y1) / 2f;
        Vector3 pos = alLargoX ? new Vector3(centro.x + u, y, centro.z) : new Vector3(centro.x, y, centro.z + u);
        Vector3 tam = alLargoX ? new Vector3(len, alto, e) : new Vector3(e, alto, len);
        Caja(g, "Pared", pos, tam, m);
    }

    void ParedConHueco(Transform g, Vector3 centro, bool alLargoX, float L, float H, float e,
                       float cx, float hw, float h0, float h1, Material m)
    {
        float a = cx - hw / 2f, b = cx + hw / 2f;
        Segmento(g, centro, alLargoX, -L / 2f, a, 0f, H, e, m);
        Segmento(g, centro, alLargoX, b, L / 2f, 0f, H, e, m);
        if (h0 > 0.01f) Segmento(g, centro, alLargoX, a, b, 0f, h0, e, m);
        if (h1 < H - 0.01f) Segmento(g, centro, alLargoX, a, b, h1, H, e, m);
    }

    void Triangulo(Transform g, Vector3 baseCentro, float semiD, float alto, Material m)
    {
        var go = new GameObject("Fronton");
        go.transform.SetParent(g, false);
        go.transform.localPosition = baseCentro;
        var mesh = new Mesh();
        mesh.vertices = new[] {
            new Vector3(0, 0, -semiD), new Vector3(0, 0, semiD), new Vector3(0, alto, 0),
            new Vector3(0, 0, -semiD), new Vector3(0, 0, semiD), new Vector3(0, alto, 0),
        };
        mesh.triangles = new[] { 0, 1, 2, 5, 4, 3 };
        mesh.RecalculateNormals();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = m;
    }

    // ================= Casas y edificios =================

    GameObject Casa(Transform padre, string nombre, Vector3 pos, float rotY, float w, float d,
                    float hp, float ph, float puerta, bool pilotes,
                    Material pared, Material techo, Material madera, Interior tipo)
    {
        var t = Raiz(padre, nombre, pos, rotY);
        float e = 0.12f;

        // Estructura inferior
        if (pilotes)
        {
            float px = w / 2f - 0.2f, pz = d / 2f - 0.2f;
            foreach (float x in new[] { -px, 0f, px })
                foreach (float z in new[] { -pz, pz })
                    Caja(t, "Pilote", new Vector3(x, (hp - 0.8f) / 2f, z), new Vector3(0.22f, hp + 0.8f, 0.22f), madera);
        }
        else
        {
            Caja(t, "Base", new Vector3(0, (hp - 0.16f - 0.5f) / 2f, 0), new Vector3(w + 0.2f, hp - 0.16f + 0.5f, d + 0.2f), mCemento);
        }
        Caja(t, "Piso", new Vector3(0, hp - 0.08f, 0), new Vector3(w, 0.16f, d), madera);

        // Paredes
        Caja(t, "Pared_Fondo", new Vector3(0, hp + ph / 2f, -d / 2f + e / 2f), new Vector3(w, ph, e), pared);
        ParedConHueco(t, new Vector3(0, hp, d / 2f - e / 2f), true, w, ph, e, 0f, puerta, 0f, 2.1f, pared);
        foreach (float s in new[] { -1f, 1f })
            ParedConHueco(t, new Vector3(s * (w / 2f - e / 2f), hp, 0f), false, d - 2f * e, ph, e, 0f, 1.0f, 1.0f, 1.8f, pared);

        // Techo a dos aguas
        float alfaDeg = 28f;
        float alfa = alfaDeg * Mathf.Deg2Rad;
        float semi = d / 2f + 0.6f;
        float aleroY = hp + ph + 0.05f;
        float alza = Mathf.Tan(alfa) * semi;
        float largo = semi / Mathf.Cos(alfa);
        float yc = aleroY + alza / 2f;
        Caja(t, "Techo_N", new Vector3(0, yc, semi / 2f), new Vector3(w + 0.8f, 0.08f, largo), techo, new Vector3(alfaDeg, 0, 0));
        Caja(t, "Techo_S", new Vector3(0, yc, -semi / 2f), new Vector3(w + 0.8f, 0.08f, largo), techo, new Vector3(-alfaDeg, 0, 0));
        Caja(t, "Cumbrera", new Vector3(0, aleroY + alza + 0.02f, 0), new Vector3(w + 0.9f, 0.12f, 0.35f), madera);
        float alzaPared = Mathf.Tan(alfa) * (d / 2f);
        Triangulo(t, new Vector3(-(w / 2f - e / 2f), hp + ph, 0f), d / 2f, alzaPared, pared);
        Triangulo(t, new Vector3(w / 2f - e / 2f, hp + ph, 0f), d / 2f, alzaPared, pared);

        // Escalera de entrada
        int n = Mathf.RoundToInt(hp / 0.3f) - 1;
        for (int k = 0; k < n; k++)
        {
            float top = hp - 0.3f * (k + 1);
            Caja(t, "Escalon", new Vector3(0, top / 2f, d / 2f + 0.2f + 0.4f * k), new Vector3(puerta + 0.4f, top, 0.4f), madera);
        }

        // Interior
        float yi = hp;
        if (tipo == Interior.Vivienda)
        {
            Caja(t, "Cama", new Vector3(-w / 2f + 0.9f, yi + 0.25f, -d / 2f + 1.3f), new Vector3(1.1f, 0.5f, 2.0f), mMaderaOsc);
            Caja(t, "Manta", new Vector3(-w / 2f + 0.9f, yi + 0.53f, -d / 2f + 1.5f), new Vector3(1.0f, 0.08f, 1.5f), Mat("Manta", 0.7f, 0.25f, 0.2f));
            Caja(t, "Mesa", new Vector3(w / 4f, yi + 0.75f, -d / 4f), new Vector3(1.0f, 0.06f, 1.0f), mMadera);
            Caja(t, "Mesa_Pata", new Vector3(w / 4f, yi + 0.37f, -d / 4f), new Vector3(0.15f, 0.74f, 0.15f), mMaderaOsc);
            Caja(t, "Banco", new Vector3(w / 4f + 0.9f, yi + 0.22f, -d / 4f), new Vector3(0.4f, 0.44f, 0.9f), mMaderaOsc);
        }
        else if (tipo == Interior.Salud)
        {
            var sabana = Mat("Sabana", 0.85f, 0.92f, 0.95f);
            for (int i = 0; i < 2; i++)
            {
                float cx = -w / 2f + 1.3f + i * 1.9f;
                Caja(t, "Camilla", new Vector3(cx, yi + 0.4f, -d / 2f + 1.5f), new Vector3(1.0f, 0.1f, 2.0f), mMaderaOsc);
                Caja(t, "Camilla_Pata", new Vector3(cx, yi + 0.2f, -d / 2f + 1.5f), new Vector3(0.9f, 0.4f, 1.9f), mCemento);
                Caja(t, "Sabana", new Vector3(cx, yi + 0.47f, -d / 2f + 1.5f), new Vector3(0.95f, 0.06f, 1.9f), sabana);
            }
            Caja(t, "Escritorio", new Vector3(w / 2f - 1.5f, yi + 0.4f, -d / 2f + 1.0f), new Vector3(1.8f, 0.8f, 0.8f), mMadera);
            Caja(t, "Botiquin", new Vector3(w / 2f - 1.5f, yi + 0.95f, -d / 2f + 1.0f), new Vector3(0.45f, 0.3f, 0.3f), Mat("Botiquin", 0.15f, 0.6f, 0.3f));
            Caja(t, "Armario", new Vector3(w / 2f - 0.4f, yi + 1.0f, 0.5f), new Vector3(0.6f, 2.0f, 1.4f), mBlanco);
            // Cruz roja sobre la puerta
            float yc2 = hp + 2.1f + (ph - 2.1f) / 2f;
            Caja(t, "Cruz_H", new Vector3(0, yc2, d / 2f + 0.03f), new Vector3(0.7f, 0.2f, 0.05f), mRojo);
            Caja(t, "Cruz_V", new Vector3(0, yc2, d / 2f + 0.03f), new Vector3(0.2f, 0.7f, 0.05f), mRojo);
        }
        else
        {
            Caja(t, "Mesa", new Vector3(0, yi + 0.4f, -d / 4f), new Vector3(1.6f, 0.8f, 0.8f), mMaderaOsc);
            Caja(t, "Caja_Herramientas", new Vector3(-w / 2f + 0.8f, yi + 0.25f, -d / 2f + 0.6f), new Vector3(0.8f, 0.5f, 0.5f), mRojo);
            Caja(t, "Estante", new Vector3(w / 2f - 0.4f, yi + 1.0f, -d / 4f), new Vector3(0.5f, 2.0f, 1.6f), mMaderaOsc);
        }
        return t.gameObject;
    }

    void Puesto(Transform padre, Vector3 pos, float rotY, Material toldo)
    {
        var t = Raiz(padre, "Puesto_Mercado", pos, rotY);
        Caja(t, "Mostrador", new Vector3(0, 0.45f, 0.3f), new Vector3(2.8f, 0.9f, 0.9f), mMadera);
        Caja(t, "Repisa", new Vector3(0, 1.2f, -0.9f), new Vector3(2.8f, 0.08f, 0.5f), mMaderaOsc);
        foreach (float sx in new[] { -1.5f, 1.5f })
            foreach (float sz in new[] { -1.0f, 1.0f })
                Caja(t, "Poste", new Vector3(sx, 1.25f, sz), new Vector3(0.12f, 2.5f, 0.12f), mMaderaOsc);
        Caja(t, "Toldo", new Vector3(0, 2.6f, 0f), new Vector3(3.4f, 0.06f, 2.8f), toldo, new Vector3(10f, 0, 0));
        Caja(t, "Fruta_1", new Vector3(-0.9f, 1.02f, 0.3f), new Vector3(0.6f, 0.25f, 0.5f), Mat("Naranja", 0.95f, 0.55f, 0.1f));
        Caja(t, "Fruta_2", new Vector3(0f, 1.02f, 0.3f), new Vector3(0.6f, 0.25f, 0.5f), Mat("Platano", 0.9f, 0.85f, 0.25f));
        Caja(t, "Verdura", new Vector3(0.9f, 1.02f, 0.3f), new Vector3(0.6f, 0.25f, 0.5f), Mat("Verdura", 0.3f, 0.6f, 0.25f));
        Caja(t, "Cooler_Pescado", new Vector3(0.9f, 1.35f, -0.9f), new Vector3(0.7f, 0.3f, 0.4f), Mat("Cooler", 0.25f, 0.5f, 0.8f));
    }

    void Barril(Transform padre, float x, float z, Material m, bool tumbado)
    {
        Vector3 p = P(x, z);
        var g = Cil(padre, "Barril", Vector3.zero, 0.6f, 0.9f, m);
        if (tumbado)
        {
            g.transform.position = p + new Vector3(0, 0.32f, 0);
            g.transform.rotation = Quaternion.Euler(0f, rnd.Next(360), 90f);
        }
        else
        {
            g.transform.position = p + new Vector3(0, 0.45f, 0);
        }
    }

    void Cartel(Transform padre, float x, float z, float rotY)
    {
        var t = Raiz(padre, "Cartel_Peligro", P(x, z), rotY);
        Caja(t, "Poste", new Vector3(0, 0.8f, 0), new Vector3(0.1f, 1.6f, 0.1f), mMaderaOsc);
        Caja(t, "Placa", new Vector3(0, 1.6f, 0.07f), new Vector3(1.0f, 0.8f, 0.05f), mAmarillo);
        Caja(t, "Franja", new Vector3(0, 1.6f, 0.1f), new Vector3(0.8f, 0.12f, 0.03f), mNegro);
        Caja(t, "Marca", new Vector3(0, 1.6f, 0.1f), new Vector3(0.12f, 0.55f, 0.03f), mNegro);
    }

    void Banca(Transform padre, float x, float z, float rotY)
    {
        var t = Raiz(padre, "Banca", P(x, z), rotY);
        Caja(t, "Asiento", new Vector3(0, 0.45f, 0), new Vector3(1.6f, 0.08f, 0.45f), mMadera);
        Caja(t, "Respaldo", new Vector3(0, 0.8f, -0.2f), new Vector3(1.6f, 0.4f, 0.06f), mMadera);
        Caja(t, "Pata_1", new Vector3(-0.7f, 0.22f, 0), new Vector3(0.08f, 0.44f, 0.4f), mMaderaOsc);
        Caja(t, "Pata_2", new Vector3(0.7f, 0.22f, 0), new Vector3(0.08f, 0.44f, 0.4f), mMaderaOsc);
    }

    void Pozo(Transform padre, float x, float z)
    {
        var t = Raiz(padre, "Pozo", P(x, z), 0f);
        Cil(t, "Brocal", new Vector3(0, 0.45f, 0), 1.8f, 0.9f, mCemento);
        Cil(t, "Agua", new Vector3(0, 0.88f, 0), 1.4f, 0.05f, Mat("AguaPozo", 0.2f, 0.25f, 0.3f, 0.8f));
        Caja(t, "Poste_1", new Vector3(-0.9f, 1.4f, 0), new Vector3(0.12f, 2.0f, 0.12f), mMaderaOsc);
        Caja(t, "Poste_2", new Vector3(0.9f, 1.4f, 0), new Vector3(0.12f, 2.0f, 0.12f), mMaderaOsc);
        Caja(t, "Techito", new Vector3(0, 2.5f, 0), new Vector3(2.4f, 0.08f, 1.6f), mOxido);
    }

    void TanqueAgua(Transform padre, float x, float z)
    {
        var t = Raiz(padre, "Tanque_Agua", P(x, z), 0f);
        foreach (float sx in new[] { -0.7f, 0.7f })
            foreach (float sz in new[] { -0.7f, 0.7f })
                Caja(t, "Pata", new Vector3(sx, 1.2f, sz), new Vector3(0.14f, 2.4f, 0.14f), mMaderaOsc);
        Caja(t, "Plataforma", new Vector3(0, 2.45f, 0), new Vector3(2.0f, 0.12f, 2.0f), mMaderaOsc);
        Cil(t, "Tanque", new Vector3(0, 3.35f, 0), 1.6f, 1.7f, Mat("TanqueAzul", 0.2f, 0.45f, 0.8f, 0.4f));
    }

    void Huerto(Transform padre, float x, float z, List<GameObject> plantas, List<GameObject> cerca)
    {
        var t = Raiz(padre, "Huerto", P(x, z), 0f);
        Caja(t, "Tierra", new Vector3(0, 0.05f, 0), new Vector3(6f, 0.12f, 4f), Mat("Tierra", 0.3f, 0.2f, 0.12f, 0.05f));
        for (int i = 0; i < 10; i++)
            PonerPP(plantas, padre, x - 2.5f + (i % 5) * 1.25f, z - 1f + (i / 5) * 2f, rnd.Next(360), 1f, Tipo.Suave);
        float hx = 3.6f, hz = 2.6f;
        LineaCerca(cerca, padre, x - hx, z - hz, x + hx, z - hz);
        LineaCerca(cerca, padre, x + hx, z - hz, x + hx, z + hz);
        LineaCerca(cerca, padre, x + hx, z + hz, x - hx, z + hz);
        LineaCerca(cerca, padre, x - hx, z + hz, x - hx, z - hz);
    }

    void Balsa(Transform padre, float x, float z)
    {
        var t = Raiz(padre, "Balsa_Minera", new Vector3(x, AGUA_H + 0.1f, z), 20f);
        Caja(t, "Plataforma", new Vector3(0, 0, 0), new Vector3(5f, 0.3f, 3.5f), mMaderaOsc);
        Caja(t, "Motor", new Vector3(1.5f, 0.6f, 0), new Vector3(1f, 0.8f, 0.8f), mRojo);
        Cil(t, "Tubo", new Vector3(-1.2f, 0.9f, 0), 0.25f, 3.5f, mGris, new Vector3(0, 0, 40f));
        Cil(t, "Barril", new Vector3(-1.6f, 0.6f, 1.0f), 0.6f, 0.9f, mGris);
    }

    void Canaleta(Transform padre, float x, float z, float rotY)
    {
        var t = Raiz(padre, "Canaleta_Lavado", P(x, z), rotY);
        Caja(t, "Fondo", new Vector3(0, 1.2f, 0), new Vector3(1.4f, 0.1f, 7f), mOxido, new Vector3(-12f, 0, 0));
        Caja(t, "Lado_1", new Vector3(-0.7f, 1.4f, 0), new Vector3(0.08f, 0.4f, 7f), mOxido, new Vector3(-12f, 0, 0));
        Caja(t, "Lado_2", new Vector3(0.7f, 1.4f, 0), new Vector3(0.08f, 0.4f, 7f), mOxido, new Vector3(-12f, 0, 0));
        Caja(t, "Soporte_1", new Vector3(0, 0.6f, -3f), new Vector3(1.2f, 1.2f, 0.15f), mMaderaOsc);
        Caja(t, "Soporte_2", new Vector3(0, 0.3f, 3f), new Vector3(1.2f, 0.6f, 0.15f), mMaderaOsc);
    }

    void Puente(Transform padre)
    {
        float largo = anchoRio + 2f * ORILLA + 4f;
        Vector3 c = new Vector3(PTE_C.x, 0f, PTE_C.y);
        Vector3 dir = new Vector3(PTE_N.x, 0f, PTE_N.y);
        Vector3 pN = c + dir * (largo / 2f);
        Vector3 pS = c - dir * (largo / 2f);
        float y = Mathf.Max(Suelo(pN.x, pN.z), Suelo(pS.x, pS.z)) + 0.05f;

        var t = Raiz(padre, "Puente", new Vector3(c.x, y, c.z), 0f);
        t.rotation = Quaternion.LookRotation(dir, Vector3.up);

        Caja(t, "Tablero", new Vector3(0, -0.1f, 0), new Vector3(3.2f, 0.2f, largo), mMadera);
        foreach (float s in new[] { -1f, 1f })
        {
            Caja(t, "Baranda_Alta", new Vector3(s * 1.5f, 1.0f, 0), new Vector3(0.1f, 0.1f, largo), mMaderaOsc);
            Caja(t, "Baranda_Baja", new Vector3(s * 1.5f, 0.5f, 0), new Vector3(0.08f, 0.08f, largo), mMaderaOsc);
            for (float u = -largo / 2f + 0.5f; u <= largo / 2f - 0.4f; u += 3f)
                Caja(t, "Poste", new Vector3(s * 1.5f, 0.55f, u), new Vector3(0.14f, 1.1f, 0.14f), mMaderaOsc);
        }
        float hp = y - (RIO_FONDO - 0.5f);
        foreach (float u in new[] { -(anchoRio / 2f + 1f), anchoRio / 2f + 1f })
            foreach (float s in new[] { -1.2f, 1.2f })
                Caja(t, "Pilar", new Vector3(s, -hp / 2f, u), new Vector3(0.4f, hp, 0.4f), mMaderaOsc);
    }

    void Muelle(Transform padre, float x)
    {
        float rz = RioZ(x);
        float yDeck = Suelo(x, rz + 9f) + 0.15f;
        var t = Raiz(padre, "Muelle", new Vector3(x, yDeck, rz + 5.5f), 0f);
        Caja(t, "Tablero", new Vector3(0, -0.08f, 0), new Vector3(2f, 0.16f, 7f), mMadera);
        float h = yDeck - (RIO_FONDO - 0.4f);
        foreach (float sx in new[] { -0.9f, 0.9f })
            foreach (float sz in new[] { -3.2f, 3.2f })
                Caja(t, "Pilote", new Vector3(sx, -h / 2f, sz), new Vector3(0.22f, h, 0.22f), mMaderaOsc);

        // Canoa junto al muelle
        float s = 25f / 60f * Mathf.Cos(x / 60f);
        var canoa = Raiz(padre, "Canoa", new Vector3(x + 2.8f, AGUA_H + 0.05f, RioZ(x + 2.8f) + 2.5f), 0f);
        canoa.rotation = Quaternion.FromToRotation(Vector3.right, new Vector3(1f, 0f, s));
        Caja(canoa, "Casco", new Vector3(0, 0.15f, 0), new Vector3(4.6f, 0.5f, 1.1f), mMaderaOsc);
        Caja(canoa, "Hueco", new Vector3(0, 0.42f, 0), new Vector3(4.2f, 0.1f, 0.75f), mMadera);
        Caja(canoa, "Proa", new Vector3(2.4f, 0.3f, 0), new Vector3(0.5f, 0.35f, 0.7f), mMaderaOsc, new Vector3(0, 0, 25f));
        Caja(canoa, "Remo", new Vector3(0, 0.55f, 0.3f), new Vector3(1.6f, 0.05f, 0.1f), mMadera, new Vector3(0, 20f, 0));
    }

    void Persona(Transform padre, GameObject pf, string nombre, float x, float z, float rotY,
                 string matRopa, Color ropa, float escala, bool casco, float elev = 0f)
    {
        GameObject go;
        if (pf != null)
        {
            go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
            go.name = nombre;
            go.transform.SetParent(padre, true);
        }
        else
        {
            go = new GameObject(nombre);
            go.transform.SetParent(padre, true);
            var cuerpo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cuerpo.name = "Cuerpo";
            cuerpo.transform.SetParent(go.transform, false);
            cuerpo.transform.localPosition = new Vector3(0, 0.8f, 0);
            cuerpo.transform.localScale = new Vector3(0.55f, 0.8f, 0.55f);
            cuerpo.GetComponent<MeshRenderer>().sharedMaterial = Mat(matRopa, ropa.r, ropa.g, ropa.b);
            var cabeza = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cabeza.name = "Cabeza";
            cabeza.transform.SetParent(go.transform, false);
            cabeza.transform.localPosition = new Vector3(0, 1.75f, 0);
            cabeza.transform.localScale = new Vector3(0.34f, 0.34f, 0.34f);
            cabeza.GetComponent<MeshRenderer>().sharedMaterial = mPiel;
            DestroyImmediate(cabeza.GetComponent<Collider>());
            if (casco)
            {
                var c = Cil(go.transform, "Casco", new Vector3(0, 1.93f, 0), 0.38f, 0.14f, mAmarillo);
                DestroyImmediate(c.GetComponent<Collider>());
            }
        }
        go.transform.position = P(x, z) + new Vector3(0f, elev, 0f);
        go.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        if (pf == null) go.transform.localScale = Vector3.one * escala;
    }

    // ================= Prefabs del pack (naturaleza) =================

    Bounds Medir(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    GameObject PonerPP(List<GameObject> lista, Transform padre, float x, float z, float rotY, float escala, Tipo tipo)
    {
        if (lista == null || lista.Count == 0) return null;
        var pf = lista[rnd.Next(lista.Count)];
        var go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
        go.transform.SetParent(padre, true);
        go.transform.position = new Vector3(x, Suelo(x, z) - 0.05f, z);
        go.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        go.transform.localScale = Vector3.one * escala;

        if (tipo != Tipo.Roca)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>()) DestroyImmediate(c);
        }
        if (tipo == Tipo.Arbol)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.radius = 0.4f;
            cap.height = 4f;
            cap.center = new Vector3(0, 2f, 0);
        }
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;
        return go;
    }

    void LineaCerca(List<GameObject> lista, Transform padre, float ax, float az, float bx, float bz)
    {
        if (lista == null || lista.Count == 0) return;
        var pf = lista[0];
        var tmp = (GameObject)PrefabUtility.InstantiatePrefab(pf);
        tmp.transform.position = Vector3.zero;
        tmp.transform.rotation = Quaternion.identity;
        var bnd = Medir(tmp);
        DestroyImmediate(tmp);
        bool largoX = bnd.size.x >= bnd.size.z;
        float largo = Mathf.Max(0.5f, largoX ? bnd.size.x : bnd.size.z);

        Vector3 dir = new Vector3(bx - ax, 0f, bz - az);
        float total = dir.magnitude;
        if (total < 0.1f) return;
        dir /= total;
        int n = Mathf.Max(1, Mathf.CeilToInt(total / largo));
        float paso = total / n;
        Quaternion rot = Quaternion.FromToRotation(largoX ? Vector3.right : Vector3.forward, dir);
        for (int i = 0; i < n; i++)
        {
            float x = ax + dir.x * paso * (i + 0.5f);
            float z = az + dir.z * paso * (i + 0.5f);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(lista[0]);
            go.transform.SetParent(padre, true);
            go.transform.position = new Vector3(x, Suelo(x, z), z);
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * (paso / largo);
            foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;
        }
    }

    // ================= Entorno =================

    void CrearAgua(Transform padre)
    {
        var agua = GameObject.CreatePrimitive(PrimitiveType.Plane);
        agua.name = "Agua_Rio";
        DestroyImmediate(agua.GetComponent<MeshCollider>());
        agua.transform.SetParent(padre, false);
        agua.transform.position = new Vector3(TAM / 2f, AGUA_H, TAM / 2f);
        agua.transform.localScale = new Vector3(TAM / 10f, 1f, TAM / 10f);
        agua.GetComponent<MeshRenderer>().sharedMaterial = Mat("Agua_Rio", 0.42f, 0.32f, 0.18f, 0.85f);
    }

    void CrearMuros(Transform padre)
    {
        float g = 4f, alto = 80f, largo = TAM + 20f;
        Muro(padre, "Muro_O", new Vector3(-g / 2f, alto / 2f, TAM / 2f), new Vector3(g, alto, largo));
        Muro(padre, "Muro_E", new Vector3(TAM + g / 2f, alto / 2f, TAM / 2f), new Vector3(g, alto, largo));
        Muro(padre, "Muro_S", new Vector3(TAM / 2f, alto / 2f, -g / 2f), new Vector3(largo, alto, g));
        Muro(padre, "Muro_N", new Vector3(TAM / 2f, alto / 2f, TAM + g / 2f), new Vector3(largo, alto, g));
    }

    void Muro(Transform padre, string nombre, Vector3 centro, Vector3 tam)
    {
        var go = new GameObject(nombre);
        go.transform.SetParent(padre, false);
        go.transform.position = centro;
        go.AddComponent<BoxCollider>().size = tam;
    }

    void Ambiente()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.004f;
        RenderSettings.fogColor = new Color(0.6f, 0.66f, 0.62f);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type != LightType.Directional) continue;
            l.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
            l.color = new Color(1f, 0.93f, 0.8f);
            l.intensity = 1.1f;
            break;
        }
    }

    void CrearJugador(Vector2 pos)
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            c.enabled = false;
            var al = c.GetComponent<AudioListener>();
            if (al != null) al.enabled = false;
        }
        var go = new GameObject("FpsController");
        go.transform.position = new Vector3(pos.x, Suelo(pos.x, pos.y) + 1.5f, pos.y);
        var cc = go.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 0.9f, 0f);
        cc.stepOffset = 0.4f;
        cc.slopeLimit = 50f;

        var camGo = new GameObject("Camara");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(go.transform, false);
        camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 800f;
        camGo.AddComponent<AudioListener>();

        var j = go.AddComponent<JugadorFPS>();
        j.camara = camGo.transform;
    }

    Transform Zona(Transform padre, string nombre, Vector2 c)
    {
        var go = new GameObject(nombre);
        go.transform.SetParent(padre, false);
        go.transform.position = P(c.x, c.y);
        var bc = go.AddComponent<BoxCollider>();
        bc.isTrigger = true;
        bc.size = new Vector3(50, 20, 50);
        bc.center = new Vector3(0, 10, 0);
        return go.transform;
    }

    bool LibreParaBosque(float x, float z, float half)
    {
        if (Dist(x, z, COM) < 34f) return false;
        if (Dist(x, z, MER) < 32f) return false;
        if (Dist(x, z, SAL) < 26f) return false;
        if (Dist(x, z, MIN) < 70f) return false;
        if (Mathf.Abs(z - RioZ(x)) < half + ORILLA + 2f) return false;
        if (DistCaminos(x, z) < 6f) return false;
        return true;
    }

    // ================= Generar todo =================

    void Generar()
    {
        var capaPasto = BuscarCapa("Cauliflower_Green");
        var capaTierra = BuscarCapa("Pebbles_Mid_Brown_Ground");
        var capaMina = BuscarCapa("Cauliflower_Dark_Brown");
        var capaPlaza = BuscarCapa("Flatstones_Grey");
        if (capaPasto == null || capaTierra == null || capaMina == null || capaPlaza == null)
        {
            Debug.LogError("No encontré las capas de terreno de Pure Poly (Cauliflower_Green, Pebbles_Mid_Brown_Ground, Cauliflower_Dark_Brown, Flatstones_Grey).");
            return;
        }

        Carpeta("Assets/Mapa");
        mats.Clear();
        Paleta();
        rnd = new System.Random(7);
        float half = anchoRio * 0.5f;
        float largoPte = anchoRio + 2f * ORILLA + 4f;
        Vector2 extN = PTE_C + PTE_N * (largoPte / 2f);
        Vector2 extS = PTE_C - PTE_N * (largoPte / 2f);

        // ---------- Alturas ----------
        var td = new TerrainData();
        AssetDatabase.CreateAsset(td, AssetDatabase.GenerateUniqueAssetPath("Assets/Mapa/TerrenoHuepetuhe.asset"));
        td.heightmapResolution = RES;
        td.size = new Vector3(TAM, ALTO, TAM);

        var h = new float[RES, RES];
        for (int zi = 0; zi < RES; zi++)
        {
            for (int xi = 0; xi < RES; xi++)
            {
                float x = xi / (RES - 1f) * TAM;
                float z = zi / (RES - 1f) * TAM;

                float n = Mathf.PerlinNoise(x * 0.012f + SEMILLA, z * 0.012f + SEMILLA) * 5f
                        + Mathf.PerlinNoise(x * 0.05f + SEMILLA, z * 0.05f + SEMILLA) * 1f;
                float m = BASE_H + n;

                float borde = Mathf.Min(Mathf.Min(x, z), Mathf.Min(TAM - x, TAM - z));
                m += (1f - S(0f, 40f, borde)) * 10f;

                float dr = Mathf.Abs(z - RioZ(x));
                m = Mathf.Lerp(m, BASE_H, 1f - S(half, half + 30f, dr));

                m = Mathf.Lerp(m, PLATAFORMA_H, 1f - S(28f, 48f, Dist(x, z, COM)));
                m = Mathf.Lerp(m, PLATAFORMA_H, 1f - S(26f, 44f, Dist(x, z, MER)));
                m = Mathf.Lerp(m, PLATAFORMA_H, 1f - S(16f, 30f, Dist(x, z, SAL)));
                m = Mathf.Lerp(m, PLATAFORMA_H, 1f - S(45f, 65f, Dist(x, z, MIN)));

                m = Mathf.Lerp(m, RIO_FONDO, 1f - S(half, half + ORILLA, dr));

                foreach (var p in POZAS)
                {
                    float dp = Vector2.Distance(new Vector2(x, z), new Vector2(p.x, p.y));
                    m = Mathf.Lerp(m, 2.8f, 1f - S(p.z * 0.6f, p.z, dp));
                }
                // extremos del puente a la misma altura
                m = Mathf.Lerp(m, PUENTE_H, 1f - S(5f, 9f, Dist(x, z, extN)));
                m = Mathf.Lerp(m, PUENTE_H, 1f - S(5f, 9f, Dist(x, z, extS)));

                h[zi, xi] = Mathf.Clamp01(m / ALTO);
            }
        }
        td.SetHeights(0, 0, h);

        // ---------- Texturas: 0 pasto, 1 tierra, 2 tierra de mina, 3 plaza ----------
        td.terrainLayers = new[] { capaPasto, capaTierra, capaMina, capaPlaza };
        td.alphamapResolution = 512;
        td.baseMapResolution = 512;
        int AR = td.alphamapResolution;
        var a = new float[AR, AR, 4];
        for (int zi = 0; zi < AR; zi++)
        {
            for (int xi = 0; xi < AR; xi++)
            {
                float x = xi / (AR - 1f) * TAM;
                float z = zi / (AR - 1f) * TAM;
                float dr = Mathf.Abs(z - RioZ(x));

                float wPlaza = Mathf.Max(1f - S(10f, 20f, Dist(x, z, MER)),
                               Mathf.Max(1f - S(7f, 13f, Dist(x, z, SAL)), 1f - S(5f, 9f, Dist(x, z, COM))));
                wPlaza = Mathf.Clamp01(wPlaza);

                float wMina = 1f - S(30f, 55f, Dist(x, z, MIN));
                wMina = Mathf.Min(Mathf.Clamp01(wMina), 1f - wPlaza);

                float wTierra = Mathf.Max(1f - S(half, half + 8f, dr), 1f - S(1.5f, 3.5f, DistCaminos(x, z)));
                wTierra = Mathf.Max(wTierra, 1f - S(10f, 22f, Dist(x, z, COM)));
                wTierra = Mathf.Min(Mathf.Clamp01(wTierra), 1f - wPlaza - wMina);

                a[zi, xi, 0] = 1f - wPlaza - wMina - wTierra;
                a[zi, xi, 1] = wTierra;
                a[zi, xi, 2] = wMina;
                a[zi, xi, 3] = wPlaza;
            }
        }
        td.SetAlphamaps(0, 0, a);

        // ---------- Terreno, agua, límites, ambiente ----------
        var terGo = Terrain.CreateTerrainGameObject(td);
        terGo.name = "Terreno_Huepetuhe";
        terGo.transform.position = Vector3.zero;
        ter = terGo.GetComponent<Terrain>();

        var raiz = new GameObject("Mapa_Huepetuhe");
        terGo.transform.SetParent(raiz.transform, true);
        CrearAgua(raiz.transform);
        CrearMuros(raiz.transform);
        Ambiente();

        // ---------- Prefabs de naturaleza ----------
        var arbolesPf = Cargar("PP_Tree_");
        var abedulesPf = Cargar("PP_Birch_Tree_");
        var pastoPf = Cargar("PP_Grass_");
        var praderaPf = Cargar("PP_Meadow_0");
        var floresPf = new List<GameObject>();
        floresPf.AddRange(Cargar("PP_Sunflower_"));
        floresPf.AddRange(Cargar("PP_Hyacinth_"));
        floresPf.AddRange(Cargar("PP_Daffodil_"));
        var rocasPf = new List<GameObject>();
        rocasPf.AddRange(Cargar("PP_Rock_Moss_Grown_"));
        rocasPf.AddRange(Cargar("PP_Rock_Pile_Forest_Moss_"));
        var cercaPf = Cargar("PP_Small_Fence_");
        var cerrosPf = Cargar("PP_Forest_Mountain_Moss_");

        // ---------- COMUNIDAD ----------
        var comunidad = Zona(raiz.transform, "Zona_Comunidad", COM);
        float[] angulos = { 20f, 90f, 140f, 190f, 240f, 290f };
        for (int i = 0; i < angulos.Length; i++)
        {
            float ang = angulos[i] * Mathf.Deg2Rad;
            float hx = COM.x + Mathf.Cos(ang) * 19f;
            float hz = COM.y + Mathf.Sin(ang) * 19f;
            float rot = Mathf.Atan2(-Mathf.Cos(ang), -Mathf.Sin(ang)) * Mathf.Rad2Deg;
            bool grande = i % 2 == 0;
            Casa(comunidad, "Casa_" + (i + 1), P(hx, hz), rot,
                 grande ? 6f : 5f, grande ? 4.6f : 4.2f, 1.2f, 2.4f, 1.1f, true,
                 paredes[rnd.Next(paredes.Length)], techos[rnd.Next(techos.Length)], mMadera, Interior.Vivienda);
        }
        Pozo(comunidad, COM.x, COM.y);
        TanqueAgua(comunidad, COM.x + 7f, COM.y + 5f);
        Banca(comunidad, COM.x - 5f, COM.y + 2f, 90f);
        Banca(comunidad, COM.x + 4f, COM.y - 4f, -90f);
        Huerto(comunidad, COM.x - 10f, COM.y - 1f, floresPf, cercaPf);
        Huerto(comunidad, COM.x + 3f, COM.y - 10f, floresPf, cercaPf);
        Muelle(comunidad, 150f);
        Cartel(comunidad, 143f, RioZ(143f) + half + ORILLA + 1.5f, 0f);
        Cartel(comunidad, 160f, RioZ(160f) + half + ORILLA + 1.5f, 0f);

        // ---------- MERCADO ----------
        var mercado = Zona(raiz.transform, "Zona_Mercado", MER);
        Material[] toldos = { Mat("Toldo_Rojo", 0.8f, 0.2f, 0.15f), Mat("Toldo_Verde", 0.2f, 0.55f, 0.3f),
                              Mat("Toldo_Azul", 0.2f, 0.4f, 0.75f), Mat("Toldo_Naranja", 0.95f, 0.55f, 0.1f) };
        for (int i = 0; i < 4; i++)
        {
            float px = MER.x - 10.5f + i * 7f;
            Puesto(mercado, P(px, MER.y - 8f), 0f, toldos[i % toldos.Length]);
            Puesto(mercado, P(px, MER.y + 8f), 180f, toldos[(i + 2) % toldos.Length]);
        }
        Banca(mercado, MER.x - 3f, MER.y, 0f);
        Banca(mercado, MER.x + 3f, MER.y, 0f);
        Barril(mercado, MER.x, MER.y + 1.5f, Mat("BarrilAzul", 0.2f, 0.35f, 0.7f), false);
        Barril(mercado, MER.x + 0.8f, MER.y + 1.8f, Mat("BarrilAzul", 0.2f, 0.35f, 0.7f), false);

        // ---------- PUESTO DE SALUD ----------
        var salud = Zona(raiz.transform, "Zona_PuestoSalud", SAL);
        Casa(salud, "Puesto_de_Salud", P(SAL.x, SAL.y), 180f, 11f, 7f, 0.3f, 3.2f, 1.5f, false,
             mBlanco, mRojo, mMadera, Interior.Salud);
        Banca(salud, SAL.x - 4f, SAL.y - 6f, 0f);
        Banca(salud, SAL.x + 4f, SAL.y - 6f, 0f);

        // ---------- PUENTE ----------
        var puenteZona = Zona(raiz.transform, "Zona_Puente", PTE_C);
        Puente(puenteZona);

        // ---------- ZONA MINERA ----------
        var mina = Zona(raiz.transform, "Zona_Minera", MIN);
        Casa(mina, "Caseta_Minera_1", P(285f, 135f), 180f, 7f, 5f, 0.3f, 2.6f, 1.1f, false,
             mOxido, mGris, mMaderaOsc, Interior.Caseta);
        Casa(mina, "Caseta_Minera_2", P(320f, 130f), 200f, 6f, 4.5f, 0.3f, 2.6f, 1.1f, false,
             mGris, mOxido, mMaderaOsc, Interior.Caseta);
        Balsa(mina, POZAS[0].x, POZAS[0].y);
        Canaleta(mina, 300f, 112f, 40f);
        var barrilHg = Mat("BarrilGris", 0.45f, 0.47f, 0.5f);
        var barrilRojo = Mat("BarrilRojo", 0.7f, 0.15f, 0.12f);
        for (int i = 0; i < 8; i++)
            Barril(mina, 290f + (i % 4) * 1.0f, 143f + (i / 4) * 1.0f, i % 2 == 0 ? barrilHg : barrilRojo, i == 3 || i == 6);
        // Cercas y carteles alrededor de las pozas
        foreach (var p in POZAS)
        {
            float rc = p.z + 4f;
            int lados = 16;
            for (int i = 0; i < lados; i++)
            {
                float a0 = i * Mathf.PI * 2f / lados, a1 = (i + 1) * Mathf.PI * 2f / lados;
                LineaCerca(cercaPf, mina, p.x + Mathf.Cos(a0) * rc, p.y + Mathf.Sin(a0) * rc,
                           p.x + Mathf.Cos(a1) * rc, p.y + Mathf.Sin(a1) * rc);
            }
            for (int i = 0; i < 3; i++)
            {
                float ang = (i * 120f + 30f) * Mathf.Deg2Rad;
                Cartel(mina, p.x + Mathf.Cos(ang) * (rc + 2f), p.y + Mathf.Sin(ang) * (rc + 2f),
                       Mathf.Atan2(Mathf.Cos(ang), Mathf.Sin(ang)) * Mathf.Rad2Deg);
            }
        }
        for (int i = 0; i < 14; i++)
        {
            float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
            float rad = 12f + (float)rnd.NextDouble() * 45f;
            float x = MIN.x + Mathf.Cos(ang) * rad, z = MIN.y + Mathf.Sin(ang) * rad;
            if (EnPozo(x, z, 8f)) continue;
            PonerPP(rocasPf, mina, x, z, rnd.Next(360), escalaNat, Tipo.Roca);
        }

        // ---------- NATURALEZA ----------
        var bosque = new GameObject("Naturaleza");
        bosque.transform.SetParent(raiz.transform, false);

        int puestos = 0, intentos = 0;
        while (puestos < arboles && intentos < arboles * 30)
        {
            intentos++;
            float x = 10f + (float)rnd.NextDouble() * (TAM - 20f);
            float z = 10f + (float)rnd.NextDouble() * (TAM - 20f);
            if (!LibreParaBosque(x, z, half)) continue;
            var lista = (rnd.Next(100) < 80 || abedulesPf.Count == 0) ? arbolesPf : abedulesPf;
            float esc = escalaNat * (0.85f + (float)rnd.NextDouble() * 0.4f);
            if (PonerPP(lista, bosque.transform, x, z, rnd.Next(360), esc, Tipo.Arbol) != null) puestos++;
        }

        puestos = 0; intentos = 0;
        while (puestos < pasto && intentos < pasto * 30)
        {
            intentos++;
            float x = 10f + (float)rnd.NextDouble() * (TAM - 20f);
            float z = 10f + (float)rnd.NextDouble() * (TAM - 20f);
            if (!LibreParaBosque(x, z, half)) continue;
            int r = rnd.Next(100);
            var lista = r < 50 ? pastoPf : (r < 80 ? praderaPf : floresPf);
            if (PonerPP(lista, bosque.transform, x, z, rnd.Next(360), 1f, Tipo.Suave) != null) puestos++;
        }

        // Rocas en las orillas del río
        puestos = 0; intentos = 0;
        while (puestos < 40 && intentos < 1200)
        {
            intentos++;
            float x = 10f + (float)rnd.NextDouble() * (TAM - 20f);
            float lado = rnd.Next(2) == 0 ? 1f : -1f;
            float z = RioZ(x) + lado * (half + 2f + (float)rnd.NextDouble() * (ORILLA + 4f));
            if (z < 10f || z > TAM - 10f) continue;
            if (DistCaminos(x, z) < 6f) continue;
            if (Dist(x, z, MIN) < 50f) continue;
            if (PonerPP(rocasPf, bosque.transform, x, z, rnd.Next(360), escalaNat, Tipo.Roca) != null) puestos++;
        }

        // Cerros de fondo en los bordes
        for (int i = 0; i < 12; i++)
        {
            float t = (float)rnd.NextDouble() * TAM;
            int lado = i % 4;
            float x = lado == 0 ? 12f : (lado == 1 ? TAM - 12f : t);
            float z = lado == 2 ? 12f : (lado == 3 ? TAM - 12f : t);
            PonerPP(cerrosPf, bosque.transform, x, z, rnd.Next(360), escalaNat * 2f, Tipo.Roca);
        }

        // ---------- PERSONAJES ----------
        var mujer = new Color(0.8f, 0.3f, 0.35f);
        Persona(comunidad, familia, "Madre", COM.x + 3f, COM.y + 3f, 200f, "Ropa_Madre", mujer, 1f, false);
        Persona(comunidad, null, "Padre", COM.x + 4.5f, COM.y + 3.5f, 190f, "Ropa_Padre", new Color(0.25f, 0.4f, 0.7f), 1f, false);
        Persona(comunidad, null, "Nino", COM.x + 3.8f, COM.y + 1.8f, 180f, "Ropa_Nino", new Color(0.9f, 0.75f, 0.2f), 0.6f, false);
        Persona(comunidad, null, "Abuela", COM.x - 4f, COM.y + 3f, 90f, "Ropa_Abuela", new Color(0.6f, 0.45f, 0.7f), 0.9f, false);
        for (int i = 0; i < 4; i++)
            Persona(mercado, i == 0 ? vendedor : null, "Vendedor_" + (i + 1), MER.x - 10.5f + i * 7f, MER.y - 8.3f, 0f,
                    "Ropa_Vendedor", new Color(0.9f, 0.85f, 0.7f), 1f, false);
        for (int i = 1; i < 4; i += 2)
            Persona(mercado, null, "Vendedora_" + i, MER.x - 10.5f + i * 7f, MER.y + 8.3f, 180f,
                    "Ropa_Vendedora", new Color(0.85f, 0.45f, 0.5f), 1f, false);
        Persona(mercado, null, "Cliente", MER.x, MER.y - 3f, 20f, "Ropa_Cliente", new Color(0.3f, 0.55f, 0.4f), 1f, false);
        Persona(salud, doctor, "Doctor", SAL.x + 3.5f, SAL.y - 1.5f, 180f, "Ropa_Doctor", new Color(0.95f, 0.95f, 0.95f), 1f, false, 0.3f);
        Persona(salud, null, "Enfermera", SAL.x - 2f, SAL.y - 3f, 160f, "Ropa_Enfermera", new Color(0.6f, 0.85f, 0.9f), 1f, false, 0.3f);
        Persona(mina, minero, "Minero_1", 296f, 128f, 180f, "Ropa_Minero", new Color(0.8f, 0.45f, 0.1f), 1f, true);
        Persona(mina, null, "Minero_2", 305f, 122f, 220f, "Ropa_Minero", new Color(0.8f, 0.45f, 0.1f), 1f, true);
        Persona(mina, null, "Minero_3", 258f, 112f, 90f, "Ropa_Minero", new Color(0.8f, 0.45f, 0.1f), 1f, true);

        // ---------- JUGADOR ----------
        CrearJugador(new Vector2(140f, 262f));

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = raiz;
        Debug.Log("Mapa Huepetuhe generado. Guarda la escena (File > Save As) y dale Play.");
    }
}
