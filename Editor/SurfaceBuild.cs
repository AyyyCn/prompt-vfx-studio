using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Arcade.VfxAuthor.Editor
{
    sealed class SurfaceBuild : IDisposable
    {
        public GameObject Root { get; private set; }
        readonly List<Object> temporary = new List<Object>();

        public SurfaceBuild(JObject draft,string assetFolder = null)
        {
            SurfaceProgram.Validate(draft);
            Root = new GameObject((string)draft["name"]);
            Root.SetActive(false);
            try
            {
                string wrapper = SurfaceProgram.Resource("SurfacePass.txt");
                var renderers = new List<Renderer>();
                foreach (var layer in draft["layers"])
                {
                    string id = (string)layer["id"];
                    string shaderKey = Guid.NewGuid().ToString("N") + "/" + id;
                    string code = wrapper.Replace("__KEY__",shaderKey).Replace("__BLEND__",(string)layer["blend"]=="alpha"?"SrcAlpha OneMinusSrcAlpha":"SrcAlpha One")
                        .Replace("__HELPERS__",(string)layer["helpers"]).Replace("__FORM__",(string)layer["geometry"]).Replace("__INK__",(string)layer["shading"]);
                    Shader shader;
                    if (assetFolder == null)
                    {
                        shader = ShaderUtil.CreateShaderAsset(code,false);
                        temporary.Add(shader);
                    }
                    else
                    {
                        string path = assetFolder + "/" + id + ".shader";
                        File.WriteAllText(path,code);
                        AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                        shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    }
                    if (!shader) throw new FormatException("Unable to import shader for "+id);
                    var material = new Material(shader) { name = id, renderQueue = 3000+renderers.Count };
                    temporary.Add(material);
                    ShaderUtil.CompilePass(material,0,true);
                    var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity.ToString()=="Error").Take(4).ToArray();
                    if (errors.Length!=0 || !shader.isSupported)
                        throw new FormatException(id+": "+(errors.Length==0?"shader unsupported on the current device":string.Join("\n",errors.Select(e=>e.message))));
                    var mesh = Grid((int)layer["segmentsU"],(int)layer["segmentsV"],(int)layer["copies"]);
                    mesh.name = id; temporary.Add(mesh);
                    if (assetFolder != null)
                    {
                        AssetDatabase.CreateAsset(material,assetFolder+"/"+id+".mat"); temporary.Remove(material);
                        AssetDatabase.CreateAsset(mesh,assetFolder+"/"+id+".asset"); temporary.Remove(mesh);
                    }
                    var child = new GameObject(id);
                    child.transform.SetParent(Root.transform,false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = child.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderers.Add(renderer);
                }
                var clock = Root.AddComponent<GeneratedSurfaceClock>();
                clock.duration=(float)draft["duration"];clock.source=draft.ToString(Formatting.None);
                clock.surfaces=renderers.ToArray();
                Root.SetActive(true);
            }
            catch { Dispose(); throw; }
        }

        static Mesh Grid(int u,int v,int copies)
        {
            int stride=u+1, vertices=stride*(v+1)*copies;
            var pos=new Vector3[vertices];var uv=new Vector2[vertices];var copy=new Vector2[vertices];
            var indices=new int[u*v*copies*6];int k=0;
            for(int n=0;n<copies;n++)
            {
                int first=n*stride*(v+1);
                for(int y=0;y<=v;y++) for(int x=0;x<=u;x++)
                {
                    int a=first+y*stride+x;
                    uv[a]=new Vector2(x/(float)u,y/(float)v);
                    copy[a]=new Vector2(n,copies);
                    if(x==u || y==v) continue;
                    indices[k++]=a;indices[k++]=a+stride;indices[k++]=a+1;
                    indices[k++]=a+1;indices[k++]=a+stride;indices[k++]=a+stride+1;
                }
            }
            return new Mesh { indexFormat=vertices>65535?IndexFormat.UInt32:IndexFormat.UInt16,
                vertices=pos,uv=uv,uv2=copy,triangles=indices,bounds=new Bounds(Vector3.zero,Vector3.one*20) };
        }

        public static GameObject Save(JObject draft,string path)
        {
            if (!path.StartsWith("Assets/",StringComparison.Ordinal) || !path.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a prefab path inside Assets.");
            string folder=AssetDatabase.GenerateUniqueAssetPath(path.Substring(0,path.Length-7)+" Sources");
            string guid=AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\','/'),Path.GetFileName(folder));
            if(string.IsNullOrEmpty(guid)) throw new IOException("Could not create the source folder.");
            try
            {
                using var build=new SurfaceBuild(draft,folder);
                var prefab=PrefabUtility.SaveAsPrefabAsset(build.Root,path,out bool ok);
                if(!ok || !prefab) throw new IOException("Could not save the generated prefab.");
                return prefab;
            }
            catch { AssetDatabase.DeleteAsset(folder); throw; }
        }

        public void Dispose()
        {
            if(Root) Object.DestroyImmediate(Root);
            for(int i=temporary.Count-1;i>=0;i--) if(temporary[i]) Object.DestroyImmediate(temporary[i]);
            temporary.Clear();
        }
    }
}
