using System.Net;
using System.Numerics;
using System.Text.Json;
using EgoEngineLibrary.Archive.Erp;

namespace WheelSwap.Core;

/// <summary>
/// An interactive 3D preview of a build: the new wheel (and the car's wheel before, for comparison) and, on the
/// preview page, the whole car around it. Real geometry, the materials the car's material binding gives each slot and
/// their textures, shaded the way the EGO vehicle shaders work as the F1 Universal Exporter documents them (surface
/// map G = roughness, B = metallic; normal map B stored as 0; decals on the second UV set; carbon = tiled pattern
/// under a lacquer). A self-contained WebGL2 page: no internet, no libraries.
/// </summary>
public static class WheelPreview
{
    /// <summary>A full page for every car of a build, with a Steering wheel / Car toggle.</summary>
    public static string Page(GameData game, SerpsTemplate tpl, BuildResult res, int textureSize = 1024)
    {
        var sc = new PreviewScene(game) { TexSize = textureSize, PaintSize = textureSize * 2 };
        var cars = res.Entries.Select(be => Car(sc, game, tpl, res, be, withCar: true)).ToList();
        // two cars of one option (MyTeam's paints share a folder): name them by their file, e.g. "MyTeam · FOM Chassis · azure"
        for (int i = 0; i < cars.Count; i++)
            if (cars.Count(c => (string)c["title"]! == (string)cars[i]["title"]!) > 1)
                cars[i]["title"] += " · " + FileLabel(res.Entries[i].Entry.FileName);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Wheel Preview</title><style>{{Css}}</style></head><body class="page">
            <div class="band"><div class="band-in"><b>Wheel preview</b><span>{{E(GameData.DonorName(res.DonorToken))}} wheel → {{E(tpl.Name)}}</span></div></div>
            <main>{{Viewer("pv", sc, cars)}}
            <p class="note">*an approximation of the in-game look: real geometry, materials and textures, simplified lighting · drag to turn, scroll to zoom, right-drag to pan</p>
            </main></body></html>
            """;
    }

    /// <summary>The wheel-only viewer block (canvas, controls, data and script) for one car; embeddable in the report.</summary>
    public static string Section(GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be, string id, int textureSize = 512)
    {
        var sc = new PreviewScene(game) { TexSize = textureSize, PaintSize = textureSize };
        return Viewer(id, sc, [Car(sc, game, tpl, res, be, withCar: false)]);
    }

    static string Viewer(string id, PreviewScene sc, List<Dictionary<string, object?>> cars)
    {
        sc.Finish();
        var data = new Dictionary<string, object?> { ["tex"] = sc.Textures, ["mats"] = sc.Materials, ["geo"] = sc.Geometry, ["cars"] = cars };
        // relaxed escaping keeps base64 "+" as is (not +); "</" is escaped below, so the JSON cannot end the script block
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var options = cars.Count > 1
            ? $"<select class=\"pv-car\">{string.Join("", cars.Select((c, i) => $"<option value=\"{i}\">{E((string)c["title"]!)}</option>"))}</select>"
            : "";
        var modes = cars.Any(c => c["body"] is not null)
            ? "<div class=\"pv-seg\"><button data-mode=\"wheel\" class=\"on\">Steering wheel</button><button data-mode=\"car\">Car</button></div>"
            : "";
        return $$"""
            <div class="pv" id="{{id}}">
              <div class="pv-bar">
                {{options}}{{modes}}
                <div class="pv-seg"><button data-side="before">Before</button><button data-side="after" class="on">After</button></div>
                <div class="pv-seg pv-themes"><button data-theme="purple">Purple</button><button data-theme="grey">Grey</button><button data-theme="white">White</button></div>
                <div class="pv-seg pv-views"></div>
                <div class="pv-help" tabindex="0"><button type="button">Controls</button>
                  <div class="pv-tip">
                    <div><b>Left-drag</b><span>turn the camera</span></div>
                    <div><b>Right-drag</b><span>move (hand tool) · or Shift + drag</span></div>
                    <div><b>Scroll</b><span>zoom in / out</span></div>
                    <div><b>Double-click</b><span>back to the selected view</span></div>
                    <div><b>Before / After</b><span>the car's own wheel / the new wheel</span></div>
                    <div><b>Studio</b><span>purple, grey or white background</span></div>
                  </div></div>
              </div>
              <canvas class="pv-canvas"></canvas>
              <div class="pv-msg"></div>
              <script type="application/json" class="pv-data">{{json.Replace("</", "<\\/")}}</script>
            </div>
            <script>{{Script}}
            WheelPreviewInit(document.getElementById("{{id}}"));</script>
            """;
    }

    // ---------------------------------------------------------------- data

    static Dictionary<string, object?> Car(PreviewScene sc, GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be, bool withCar)
    {
        var outErp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var target = tpl.LoadModel(game, be.Entry);
        var after = new CarModel(game, be.PrivateToken, [outErp]);
        var common = sc.Common(target.Season);
        IReadOnlyDictionary<string, ErpResource> commonIx = common is null ? new Dictionary<string, ErpResource>() : ErpX.Index(common);
        // materials and textures: the entry, then the template's other ERPs that apply to this option (its own folder, or
        // folders holding no car), then the model's game files and the shared vehicle resources
        var carFolders = tpl.CarEntries.Select(c => c.Folder).ToHashSet();
        static string FolderOf(string n) => n.Contains('/') ? n[..n.LastIndexOf('/')] : "";
        var others = tpl.EntryNames.Where(n => n != be.Entry.ZipPath && n.EndsWith(".erp", StringComparison.OrdinalIgnoreCase))
                        .Where(n => FolderOf(n) == be.Entry.Folder || !carFolders.Contains(FolderOf(n)))
                        .OrderBy(n => FolderOf(n) == be.Entry.Folder ? 0 : 1)
                        .Select(n => { try { return ErpX.Index(ErpX.Load(tpl.Get(n))); } catch (InvalidDataException) { return null; } })
                        .Where(x => x is not null).Cast<IReadOnlyDictionary<string, ErpResource>>().ToList();
        IReadOnlyDictionary<string, ErpResource> origIx = ErpX.Index(ErpX.Load(tpl.Get(be.Entry.ZipPath)));
        var lkBefore = new PreviewScene.Lookup([origIx, .. others, target.Res, commonIx]);
        var lkAfter = new PreviewScene.Lookup([ErpX.Index(outErp), origIx, .. others, target.Res, commonIx]);
        var bindBefore = PreviewScene.Binding(lkBefore, be.Entry.MatBindings);
        var bindAfter = PreviewScene.Binding(lkAfter, be.Entry.MatBindings);

        var ntAfter = NodeTable.Parse(after.Render);
        var wAfter = PreviewScene.NodeWorld(ntAfter);
        Func<int, Matrix4x4> Bones(NodeTable nt, Func<int, Matrix4x4> w) =>
            pal => pal >= 0 && pal < nt.Palette.Length ? w(nt.Palette[pal]) : Matrix4x4.Identity;
        var ntBefore = NodeTable.Parse(target.Render);
        var sw = ntAfter.SteeringCluster().TryGetValue(("steering_wheel", "steer_master"), out var swn) ? swn : ntAfter.SteerMaster;
        Matrix4x4.Invert(wAfter(sw.Index), out var toWheel);

        int origIb = target.Buf(target.Mesh("?lod=0&mesh").IndexBuffer).Count;
        var afterMesh = after.Mesh("?lod=0&mesh");
        var wheelBox = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        var wheelAfter = sc.Groups(PreviewScene.Buffers.Of(after), afterMesh.Subs.Where(s => s.IOff >= origIb).Select(s => (afterMesh, s)),
            s => sc.Material(lkAfter, bindAfter, s.Name), Bones(ntAfter, wAfter), wheelBox);
        var beforeSubs = WheelGrafter.Family.Select(p => target.TryMesh($"?lod=0&{p}&mesh")).Where(m => m is { Empty: false })
                                            .SelectMany(m => m!.Subs.Select(s => (m!, s)));
        var wheelBefore = sc.Groups(PreviewScene.Buffers.Of(target), beforeSubs,
            s => sc.Material(lkBefore, bindBefore, s.Name), Bones(ntBefore, PreviewScene.NodeWorld(ntBefore)), new[] { 9f, 9f, 9f, -9f, -9f, -9f });

        List<Dictionary<string, object>>? body = null;
        var box = new[] { 9f, 9f, 9f, -9f, -9f, -9f };
        if (withCar)
        {
            body = sc.Groups(PreviewScene.Buffers.Of(after), afterMesh.Subs.Where(s => s.IOff < origIb).Select(s => (afterMesh, s)),
                s => sc.Material(lkAfter, bindAfter, s.Name), Bones(ntAfter, wAfter), box);
            body.AddRange(Tyres(sc, target.Season, ntAfter, wAfter, commonIx, box));
        }

        var targetName = be.Entry.Kind == EntryKind.TeamChassis ? GameData.TeamName(be.Entry.SlotToken ?? be.ModelToken) : "FOM chassis";
        return new()
        {
            ["title"] = be.Entry.Title,
            ["afterName"] = GameData.DonorName(be.DonorToken) + " wheel",
            ["beforeName"] = targetName + " wheel (before)",
            ["toWheel"] = Mat(toWheel),
            ["box"] = withCar ? box : wheelBox,
            ["wbox"] = wheelBox,
            ["body"] = body,
            ["after"] = wheelAfter,
            ["before"] = wheelBefore,
        };
    }

    /// <summary>The shared tyre model (common.erp, shared/tyres): its four tread + wall pairs sit on the car's
    /// wheel_bl / br / fl / fr nodes.</summary>
    static IEnumerable<Dictionary<string, object>> Tyres(PreviewScene sc, int season, NodeTable nt, Func<int, Matrix4x4> w,
        IReadOnlyDictionary<string, ErpResource> commonIx, float[] box)
    {
        var pre = $"eaid://f1_{season}_vehicle_package/shared/tyres/idf/tyres";
        if (!commonIx.TryGetValue(pre + ".idf?lod=0&mesh", out var mr)) return [];
        var lk = new PreviewScene.Lookup([commonIx]);
        var bind = PreviewScene.Binding(lk, [pre + ".emb?context=default"]);
        var mesh = MeshDesc.Parse(ErpX.Data(mr));
        var corners = new[] { "wheel_bl", "wheel_br", "wheel_fl", "wheel_fr" };
        var groups = new List<Dictionary<string, object>>();
        for (int i = 0; i < corners.Length; i++)
        {
            var node = nt.Nodes.FirstOrDefault(n => n.Name == corners[i]);
            if (node is null) continue;
            var at = w(node.Index);
            var subs = mesh.Subs.Skip(i * 2).Take(2).Select(s => (mesh, s));
            groups.AddRange(sc.Groups(PreviewScene.Buffers.Of(lk), subs, s => sc.Material(lk, bind, s.Name), _ => at, box));
        }
        return groups;
    }

    /// <summary>Column-major for WebGL (System.Numerics row-vector layout is the same memory order).</summary>
    static float[] Mat(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    static string FileLabel(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var paint = System.Text.RegularExpressions.Regex.Match(stem, @"paint-([a-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return paint.Success ? paint.Groups[1].Value : System.Text.RegularExpressions.Regex.Replace(stem, @"^\d+--\d+--", "");
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    // ---------------------------------------------------------------- page

    internal const string Css = """
        .page{margin:0;background:#F9F9F9;color:#111;font:14px/1.5 "Presicav","Segoe UI",system-ui,sans-serif}
        .page .band{background:#4300AD}
        .page .band-in{max-width:1180px;margin:0 auto;padding:16px 20px;display:flex;flex-wrap:wrap;gap:6px 18px;align-items:center;justify-content:space-between;color:#F9F9F9}
        .page .band b{font-weight:800;letter-spacing:.16em;text-transform:uppercase;font-size:13px}
        .page .band span{font:700 12.5px "Roboto Mono",Consolas,monospace}
        .page main{max-width:1180px;margin:0 auto;padding:18px 20px 30px}
        .note{font:700 12px "Roboto Mono",Consolas,monospace;color:#4300AD;margin:10px 0 0}
        .pv{position:relative;background:#111111;border:1px solid #2b2140}
        .pv-bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:8px;background:#1C0040}
        .pv-seg{display:flex}
        .pv button,.pv select{font:700 10.5px "Presicav","Segoe UI",sans-serif;letter-spacing:.12em;text-transform:uppercase;color:#E9E2FF;
          background:transparent;border:1px solid #4b3a74;padding:7px 11px;cursor:pointer}
        .pv select{text-transform:none;letter-spacing:.04em}
        .pv-seg button+button{border-left:0}
        .pv button.on{background:#4300AD;border-color:#4300AD;color:#fff}
        .pv-canvas{display:block;width:100%;height:min(70vh,640px);cursor:grab;touch-action:none}
        .pv-canvas:active{cursor:grabbing}
        .pv-help{margin-left:auto;position:relative}
        .pv-help .pv-tip{display:none;position:absolute;right:0;top:calc(100% + 6px);z-index:5;background:#1C0040;border:1px solid #4300AD;
          padding:10px 14px;box-shadow:0 6px 18px rgba(0,0,0,.35);font:700 11.5px/1.9 "Roboto Mono",Consolas,monospace;color:#E9E2FF;white-space:nowrap}
        .pv-help:hover .pv-tip,.pv-help:focus-within .pv-tip{display:block}
        .pv-tip div{display:flex;gap:14px}.pv-tip b{min-width:118px;color:#fff;font-family:"Presicav","Segoe UI",sans-serif;font-size:9.5px;letter-spacing:.12em;text-transform:uppercase}
        .pv-msg{position:absolute;left:12px;bottom:10px;font:700 12px "Roboto Mono",Consolas,monospace;color:#bfb2ea;pointer-events:none}
        """;

    // A small WebGL2 renderer: GGX lighting with a key/fill light and a sky/ground environment, a lacquer coat on
    // paint and carbon, the EGO texture rules per material kind, block-compressed textures, orbit camera with presets.
    const string Script = """
        function WheelPreviewInit(root){
        if(!root)return;
        const data=JSON.parse(root.querySelector('.pv-data').textContent);
        const canvas=root.querySelector('.pv-canvas'),msg=root.querySelector('.pv-msg');
        const gl=canvas.getContext('webgl2',{antialias:true});
        if(!gl){msg.textContent='3D preview needs WebGL2 (any current browser).';return;}
        const VS=`#version 300 es
        in vec3 aPos;in vec4 aNrm;in vec4 aTan;in vec4 aUv;
        uniform mat4 uVP,uModel,uLightVP;out vec3 vPos;out vec3 vN;out vec4 vT;out vec4 vUv;out float vAo;out vec4 vSh;
        void main(){vec4 w=uModel*vec4(aPos,1.);mat3 m=mat3(uModel);vPos=w.xyz;vN=m*aNrm.xyz;vT=vec4(m*aTan.xyz,aTan.w);vUv=aUv;vAo=aNrm.w;vSh=uLightVP*w;gl_Position=uVP*w;}`;
        // shadow pass: depth from the key light
        const SVS=`#version 300 es
        in vec3 aPos;uniform mat4 uLightVP,uModel;void main(){gl_Position=uLightVP*uModel*vec4(aPos,1.);}`;
        const SFS=`#version 300 es
        precision mediump float;out vec4 o;void main(){o=vec4(1.);}`;
        const FS=`#version 300 es
        precision highp float;
        in vec3 vPos;in vec3 vN;in vec4 vT;in vec4 vUv;in float vAo;in vec4 vSh;uniform highp sampler2DShadow uShadow;uniform int uHasShadow;
        uniform sampler2D uD,uN,uS,uL,uC,uF,uLS;uniform int uKind,uStudio;uniform vec3 uRoom,uGlow,uSkyLo,uSkyHi,uFloor;uniform vec4 uHas,uHas2,uP0,uP1,uP2,uP3,uP4,uP5;uniform vec3 uEye;
        out vec4 o;
        const float PI=3.14159265;
        // car mode: a neutral dark showroom (as in the game's car viewer, without its coloured wall) lit by an overhead
        // softbox and low strip lights, so metallic paint only shows its colour where it catches a light
        vec3 studio(vec3 r,float rough){float w=mix(.02,.55,rough);vec3 c=uRoom;
          c+=vec3(2.4)*smoothstep(.8-w,.8+w*.5,r.y);
          c+=vec3(1.)*(1.-smoothstep(0.,.05+w,abs(r.y-.2)))*(.55+.45*abs(r.x));
          c+=uGlow*smoothstep(.1-w,.7,-r.x)*(1.-smoothstep(0.,.45+w,abs(r.y-.25)));
          return mix(c,uRoom+vec3(.14),rough*rough*.5);}
        vec3 env(vec3 r,float rough){if(uStudio==1)return studio(r,rough);float t=clamp(r.y*.5+.5,0.,1.);vec3 sky=mix(uSkyLo,uSkyHi,pow(t,1.6));
          float sun=pow(max(dot(r,normalize(vec3(-.3,.75,-.6))),0.),mix(400.,8.,rough));return sky+vec3(1.,.97,.92)*sun*(1.-rough)*2.;}
        float D(float nh,float a){float a2=a*a;float d=nh*nh*(a2-1.)+1.;return a2/(PI*d*d);}
        float G(float nv,float nl,float a){float k=(a+1.)*(a+1.)/8.;return nv/(nv*(1.-k)+k)*nl/(nl*(1.-k)+k);}
        vec3 F(float c,vec3 f0){return f0+(1.-f0)*pow(1.-c,5.);}
        vec3 lin(vec3 c){return pow(c,vec3(2.2));}
        vec2 strip(vec2 uv,ivec2 sz){return sz.y==0?uv:vec2(uv.x,fract(uv.y)*float(sz.x)/float(sz.y));}
        vec3 nmap(sampler2D s,vec2 uv,vec3 n){vec3 t=vT.xyz-n*dot(n,vT.xyz);if(dot(t,t)<1e-8)return n;t=normalize(t);vec3 b=cross(n,t)*(vT.w<0.?-1.:1.);
          vec2 xy=texture(s,uv).rg*2.-1.;vec3 tn=vec3(xy,sqrt(max(0.,1.-dot(xy,xy))));return normalize(t*tn.x+b*tn.y+n*tn.z);}
        // 5x5 PCF over the key light's shadow map (hardware-compared, bilinear), soft-edged
        float shadowAt(){if(uHasShadow==0)return 1.;vec3 p=vSh.xyz/vSh.w*.5+.5;if(any(lessThan(p,vec3(0.)))||any(greaterThan(p,vec3(1.))))return 1.;
          vec2 t=1./vec2(textureSize(uShadow,0));float s=0.;for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)s+=texture(uShadow,vec3(p.xy+vec2(x,y)*t,p.z-.0008));return s/25.;}
        void main(){
          vec3 n=normalize(vN);if(!gl_FrontFacing)n=-n;vec3 gn=n;float sh0=shadowAt();
          // the studio's key is a large softbox: shadow on the car is partial (the floor below keeps the full shadow)
          float sh=mix(.35,1.,sh0);
          if(uKind==9){vec2 q=abs(vPos.xz-uP0.xy)-uP0.zw;float d=length(max(q,0.))+min(max(q.x,q.y),0.);
            float cont=1.-smoothstep(-.35,.35,d);float fade=1.-smoothstep(3.5,9.,length(vPos.xz-uP0.xy));
            o=vec4(mix(uFloor,uFloor*.08,clamp((1.-sh0)*.88+cont*.3,0.,1.)),fade);return;}
          vec2 uv0=vUv.xy,uv1=vUv.zw;
          vec3 alb=vec3(.3);float rough=.5,metal=0.,alpha=1.,coat=0.,coatR=.05,occ=1.;vec3 emit=vec3(0.);vec3 mc=vec3(-1.);
          if(uKind==1){vec2 t=uv0+uP1.xy;
            // paint_s: R = lacquer mask, G = roughness, B = metallic; paint_c = the metallic areas' reflection colour
            if(uHas.x>0.)alb=lin(texture(uD,t).rgb);
            coat=1.;
            if(uHas.z>0.){vec4 s=texture(uS,t);rough=clamp(s.g,.04,1.);metal=s.b;coat=s.r;}else rough=.35;
            if(uHas2.x>0.)mc=lin(texture(uC,t).rgb)+uP5.rgb;
            if(uHas2.y>0.&&metal>0.)n=normalize(mix(n,nmap(uF,uv0*uP4.y*96.,n),uP4.x));   // flakes are microscopic: many repeats, so they average out with distance
            coatR=max(uP1.w,.03);}
          else if(uKind==2){vec2 t=uv0*uP1.z+uP0.zw;alb=vec3(.018);rough=.3;
            if(uHas.x>0.){vec4 p=texture(uD,t);rough=clamp(p.g,.05,1.);metal=p.b;}
            if(uHas.y>0.)n=nmap(uN,t,n);
            coat=uP2.y;coatR=max(uP1.w,.03);}
          else if(uKind==3){alb=vec3(.02);alpha=.25;rough=.03;if(uHas.x>0.){vec4 c=texture(uD,uv0);alb=lin(c.rgb);alpha=c.a;}}
          else if(uKind==4){alb=vec3(.006);rough=.08;emit=vec3(.004,.006,.01)*(1.+uP2.z*4.);}
          else if(uKind==5){alb=vec3(.8);metal=1.;rough=.04;}
          else if(uKind==6){alpha=0.;if(uHas.x>0.){vec4 c=texture(uD,uv1);alb=lin(c.rgb);alpha=c.a;}}
          else{vec2 t=uv0*uP0.xy,tD=t,tN=t,tS=t;
            // tread sheets stack strips as tall as the sheet is wide (8 on the colour sheet, 4 on the normal map); a new
            // tyre is the first strip of each
            if(uKind==7&&uP0.y<0.){tD=strip(uv0,textureSize(uD,0));tN=strip(uv0,textureSize(uN,0));tS=strip(uv0,textureSize(uS,0));}
            vec4 dc=uHas.x>0.?texture(uD,tD):vec4(0.);
            if(uHas.x>0.)alb=lin(dc.rgb);else if(uKind==7)alb=vec3(.035);
            // surface map: R = baked occlusion (rims: dark barrel and crevices), G = roughness, B = metallic
            if(uHas.z>0.){vec4 s=texture(uS,tS);rough=clamp(s.g,.04,1.);metal=s.b;if(uKind!=7)occ=mix(.2,1.,s.r);}
            if(uHas.y>0.)n=nmap(uN,tN,n);
            if(uKind==7){metal=0.;rough=clamp(rough,.42,.92);
              // sidewall: the colour sheet's alpha holds the moulded radial texture; the lettering mask wraps like the sidewall
              if(uP0.y>0.&&uHas.x>0.)rough=mix(rough,.5,dc.a*.6);
              if(uHas.w>0.){vec4 l=texture(uL,fract(uv0));float c=l.r*l.a;c=c<.04?0.:sqrt(c);alb=mix(alb,lin(uP3.rgb),c);rough=mix(rough,uP2.x,c);}}
            if(uKind==8)emit=alb*.12;}
          if(uHas.w>0.&&uKind!=3&&uKind!=6&&uKind!=7){vec4 l=texture(uL,uv1);float a=l.a;alb=mix(alb,lin(l.rgb),a);n=normalize(mix(n,gn,a));
            if(uHas2.z>0.){vec4 ls=texture(uLS,uv1);rough=mix(rough,clamp(ls.g,.04,1.),a);metal=mix(metal,ls.b,a);}else{rough=mix(rough,uP2.x,a);metal=mix(metal,0.,a);}
            if(mc.x>=0.)mc=mix(mc,alb,a);}
          if(alpha<.004)discard;
          float ao=(uP2.w>0.?clamp(vAo,0.,1.):1.)*occ;
          vec3 v=normalize(uEye-vPos);float nv=max(dot(n,v),1e-4),gv=max(dot(gn,v),1e-4);
          vec3 f0=mix(vec3(.04),mc.x>=0.?mc:alb,metal);vec3 col=vec3(0.);float a=rough*rough,ac=coatR*coatR;
          float Fcv=F(gv,vec3(.04)).x*coat;
          vec3 L[2];L[0]=normalize(vec3(-.35,.8,-.6));L[1]=normalize(vec3(.6,.25,.45));vec3 C[2];C[0]=vec3(3.2);C[1]=vec3(.9,.95,1.1);
          for(int i=0;i<2;i++){vec3 l=L[i];vec3 h=normalize(l+v);float nl=max(dot(n,l),0.);
            if(nl>0.){float nh=max(dot(n,h),0.);vec3 Fr=F(max(dot(h,v),0.),f0);vec3 spec=D(nh,a)*G(nv,nl,a)*Fr/(4.*nv*nl+1e-4);vec3 kd=(1.-Fr)*(1.-metal);
              col+=(kd*alb/PI+spec)*C[i]*nl*(1.-Fcv)*(i==0?sh:1.)*mix(1.,occ,.6);}
            float gnl=max(dot(gn,l),0.);if(coat>0.&&gnl>0.){float gh=max(dot(gn,h),0.);float Fc=F(max(dot(h,v),0.),vec3(.04)).x;
              col+=coat*D(gh,ac)*G(gv,gnl,ac)*Fc/(4.*gv*gnl+1e-4)*C[i]*gnl*(i==0?sh:1.);}}
          // in the studio most ambient light comes from the overhead softbox, so shadowed areas get less of it
          if(uStudio==1)ao*=mix(.7,1.,sh0);
          vec3 r=reflect(-v,n);vec3 Fe=F(nv,f0);
          col+=((1.-Fe)*(1.-metal)*alb*env(n,1.)*.55+Fe*env(r,rough)*(1.-rough*.7)*.6*uP3.w)*ao*(1.-Fcv)+Fcv*env(reflect(-v,gn),coatR)*.65+emit;
          if(uKind==3)alpha=max(alpha,Fe.x*.9);
          if(uKind==7)col+=vec3(.035)*pow(1.-nv,3.)*env(n,.8)*ao;   // rubber's soft grazing sheen
          col=col*(2.51*col+.03)/(col*(2.43*col+.59)+.14);
          o=vec4(pow(clamp(col,0.,1.),vec3(1./2.2)),alpha);}`;
        function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw gl.getShaderInfoLog(x);return x;}
        const ATTR=['aPos','aNrm','aTan','aUv'];
        function program(vs,fs){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,vs));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,fs));
          ATTR.forEach((a,i)=>gl.bindAttribLocation(p,i,a));gl.linkProgram(p);return p;}
        const prog=program(VS,FS),sprog=program(SVS,SFS);
        if(!gl.getProgramParameter(prog,gl.LINK_STATUS)){msg.textContent='3D preview: '+gl.getProgramInfoLog(prog);return;}
        const U={};const u=n=>U[n]||(U[n]=gl.getUniformLocation(prog,n));
        function bytes(b){const s=atob(b),a=new Uint8Array(s.length);for(let i=0;i<s.length;i++)a[i]=s.charCodeAt(i);return a;}
        // every blob is zlib + base64: inflate them all once, before the first frame
        async function inflate(b){return new Uint8Array(await new Response(new Blob([bytes(b)]).stream().pipeThrough(new DecompressionStream('deflate'))).arrayBuffer());}
        // textures: block-compressed levels as shipped (BC1/2/3 s3tc, BC4/5 rgtc, BC7 bptc) or plain RGBA
        const ext={s3tc:gl.getExtension('WEBGL_compressed_texture_s3tc'),rgtc:gl.getExtension('EXT_texture_compression_rgtc'),bptc:gl.getExtension('EXT_texture_compression_bptc')};
        const aniso=gl.getExtension('EXT_texture_filter_anisotropic');
        const FMT={bc1:['s3tc',0x83F1],bc2:['s3tc',0x83F2],bc3:['s3tc',0x83F3],bc4:['rgtc',0x8DBB],bc5:['rgtc',0x8DBD],bc7:['bptc',0x8E8C]};
        const missing=new Set(),texs=[];
        function texture(i){if(i==null||i<0)return null;if(texs[i]!==undefined)return texs[i];const t=data.tex[i];if(t.f==='none')return texs[i]=null;
          if(t.f!=='rgba'&&!(FMT[t.f]&&ext[FMT[t.f][0]])){missing.add(t.f);return texs[i]=null;}
          const x=gl.createTexture();gl.activeTexture(gl.TEXTURE8);gl.bindTexture(gl.TEXTURE_2D,x);   // a scratch unit: never one a draw samples
          if(t.f==='rgba'){gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8,t.w,t.h,0,gl.RGBA,gl.UNSIGNED_BYTE,t.l[0]);gl.generateMipmap(gl.TEXTURE_2D);}
          else{t.l.forEach((l,j)=>gl.compressedTexImage2D(gl.TEXTURE_2D,j,FMT[t.f][1],Math.max(1,t.w>>j),Math.max(1,t.h>>j),0,l));
            gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAX_LEVEL,t.l.length-1);}
          t.l=null;return texs[i]=x;}
        function sampler(wrap){const s=gl.createSampler();gl.samplerParameteri(s,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.samplerParameteri(s,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
          gl.samplerParameteri(s,gl.TEXTURE_WRAP_S,wrap);gl.samplerParameteri(s,gl.TEXTURE_WRAP_T,wrap);if(aniso)gl.samplerParameterf(s,aniso.TEXTURE_MAX_ANISOTROPY_EXT,8);return s;}
        const repeat=sampler(gl.REPEAT),clamp=sampler(gl.CLAMP_TO_EDGE);
        // geometry: float32 positions, half normal/tangent/uv, 16/32-bit indices; uploaded on first draw
        function vao(g){if(g.vao)return g.vao;const v=gl.createVertexArray();gl.bindVertexArray(v);
          [['aPos','pos',3,gl.FLOAT],['aNrm','nrm',4,gl.HALF_FLOAT],['aTan','tan',4,gl.HALF_FLOAT],['aUv','uv',4,gl.HALF_FLOAT]].forEach(([a,k,n,ty])=>{
            const loc=gl.getAttribLocation(prog,a);if(loc<0)return;const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);
            gl.bufferData(gl.ARRAY_BUFFER,g[k],gl.STATIC_DRAW);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,n,ty,false,0,0);g[k]=null;});
          const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,g.idx,gl.STATIC_DRAW);g.idx=null;
          gl.bindVertexArray(null);return g.vao=v;}
        let groundV=null;
        function groundVao(){if(groundV)return groundV;const v=gl.createVertexArray();gl.bindVertexArray(v);const S=40;
          const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-S,0,-S,S,0,-S,S,0,S,-S,0,S]),gl.STATIC_DRAW);
          const loc=gl.getAttribLocation(prog,'aPos');gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,3,gl.FLOAT,false,0,0);
          const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint16Array([0,1,2,0,2,3]),gl.STATIC_DRAW);
          gl.bindVertexArray(null);return groundV=v;}
        function material(m){const t=m.t,has=[t.d,t.n,t.s,t.l].map(i=>texture(i)?1:0),has2=[t.c,t.f,t.ls,-1].map(i=>texture(i)?1:0);
          gl.uniform4fv(u('uHas2'),has2);gl.uniform4fv(u('uP4'),m.p.slice(16,20));gl.uniform4fv(u('uP5'),m.p.slice(20,24));
          [['uD',t.d,repeat],['uN',t.n,repeat],['uS',t.s,repeat],['uL',t.l,clamp],['uC',t.c,repeat],['uF',t.f,repeat],['uLS',t.ls,clamp]].forEach(([n,i,s],k)=>{gl.activeTexture(gl.TEXTURE0+k);gl.bindTexture(gl.TEXTURE_2D,texture(i));gl.bindSampler(k,s);gl.uniform1i(u(n),k);});
          gl.uniform1i(u('uKind'),m.k);gl.uniform4fv(u('uHas'),has);gl.uniform4fv(u('uP0'),m.p.slice(0,4));gl.uniform4fv(u('uP1'),m.p.slice(4,8));gl.uniform4fv(u('uP2'),m.p.slice(8,12));gl.uniform4fv(u('uP3'),m.p.slice(12,16));}
        // camera: wheel space (x across, y up, z along the car; the driver sits at -z) or car space (front = +z)
        const I4=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
        const VIEWS={wheel:[['driver',"Driver's view",{yaw:0,pitch:.2,dist:.5,fov:42,space:'wheel'}],['front','Front',{yaw:0,pitch:0,dist:.5,fov:36,space:'wheel'}],
            ['side','Side',{yaw:Math.PI/2,pitch:.05,dist:.45,fov:36,space:'wheel'}],['three','3/4',{yaw:.75,pitch:.35,dist:.5,fov:36,space:'wheel'}]],
          car:[['cockpit','Cockpit',{yaw:0,pitch:.17,dist:.62,fov:62,space:'wheel',t:[0,.04,0]}],['front','Front',{yaw:Math.PI,pitch:.1,dist:6.6,fov:30,space:'car'}],
            ['side','Side',{yaw:Math.PI/2,pitch:.05,dist:8.4,fov:30,space:'car'}],['three','3/4',{yaw:2.35,pitch:.28,dist:7.6,fov:30,space:'car'}],
            ['top','Top',{yaw:Math.PI,pitch:1.45,dist:9,fov:30,space:'car'}]]};
        const cars=data.cars;let car=0,side='after',mode='wheel',cam=null,target=[0,0,0];
        function home(view){const c=cars[car];cam={...view};
          if(view.space==='car'){const b=c.box;target=[(b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2];}else target=(view.t||[0,0,-.03]).slice();}
        function persp(f,a,n,fa){const t=1/Math.tan(f/2);return [t/a,0,0,0,0,t,0,0,0,0,(fa+n)/(n-fa),-1,0,0,2*fa*n/(n-fa),0];}
        function look(e,c,up){const z=norm(sub(e,c)),x=norm(cross(up,z)),y=cross(z,x);return [x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,-dot(x,e),-dot(y,e),-dot(z,e),1];}
        function mul(a,b){const r=new Array(16).fill(0);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[j*4+i]+=a[k*4+i]*b[j*4+k];return r;}
        const sub=(a,b)=>[a[0]-b[0],a[1]-b[1],a[2]-b[2]],dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{const l=Math.hypot(...a);return [a[0]/l,a[1]/l,a[2]/l];};
        function eye(){return [target[0]-Math.sin(cam.yaw)*Math.cos(cam.pitch)*cam.dist,target[1]+Math.sin(cam.pitch)*cam.dist,target[2]-Math.cos(cam.yaw)*Math.cos(cam.pitch)*cam.dist];}
        function drawList(list,blend){for(const g of list){const m=data.mats[g.m];if(!!m.blend!==blend)continue;const x=data.geo[g.g];material(m);gl.bindVertexArray(vao(x));
          gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}}
        let pending=false;
        // shadow map: the key light's depth, re-rendered only when what is shown changes (the light is fixed in the scene)
        const SM=4096,shadowTex=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,shadowTex);
        gl.texStorage2D(gl.TEXTURE_2D,1,gl.DEPTH_COMPONENT24,SM,SM);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_MODE,gl.COMPARE_REF_TO_TEXTURE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_FUNC,gl.LEQUAL);
        const shadowFb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,shadowFb);
        gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.TEXTURE_2D,shadowTex,0);gl.drawBuffers([gl.NONE]);gl.readBuffer(gl.NONE);
        const shadowOk=gl.checkFramebufferStatus(gl.FRAMEBUFFER)===gl.FRAMEBUFFER_COMPLETE;gl.bindFramebuffer(gl.FRAMEBUFFER,null);
        const KEY=norm([-.35,.8,-.6]);let shadowKey='',lightVP=I4;
        function ortho(r,n,f){return [1/r,0,0,0,0,1/r,0,0,0,0,-2/(f-n),0,0,0,-(f+n)/(f-n),1];}
        function xf(m,p){return [m[0]*p[0]+m[4]*p[1]+m[8]*p[2]+m[12],m[1]*p[0]+m[5]*p[1]+m[9]*p[2]+m[13],m[2]*p[0]+m[6]*p[1]+m[10]*p[2]+m[14]];}
        function updateShadow(list,model,b,key){
          if(!shadowOk||key===shadowKey)return;shadowKey=key;
          const c=xf(model,[(b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2]),r=Math.hypot(b[3]-b[0],b[4]-b[1],b[5]-b[2])*.62;
          const up=Math.abs(KEY[1])>.95?[1,0,0]:[0,1,0];
          lightVP=mul(ortho(r,.01,r*4),look([c[0]+KEY[0]*r*2,c[1]+KEY[1]*r*2,c[2]+KEY[2]*r*2],c,up));
          gl.bindFramebuffer(gl.FRAMEBUFFER,shadowFb);gl.viewport(0,0,SM,SM);gl.clear(gl.DEPTH_BUFFER_BIT);
          gl.useProgram(sprog);gl.uniformMatrix4fv(gl.getUniformLocation(sprog,'uLightVP'),false,lightVP);gl.uniformMatrix4fv(gl.getUniformLocation(sprog,'uModel'),false,model);
          gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.disable(gl.BLEND);gl.enable(gl.POLYGON_OFFSET_FILL);gl.polygonOffset(1.5,3);
          for(const g of list){if(data.mats[g.m].blend)continue;const x=data.geo[g.g];gl.bindVertexArray(vao(x));gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}
          gl.disable(gl.POLYGON_OFFSET_FILL);gl.bindVertexArray(null);gl.bindFramebuffer(gl.FRAMEBUFFER,null);}
        // studio colours (RM Designs palette): purple (#1C0040 / #4300AD glow), grey (#111111), white (#F2F2F2)
        const THEMES={purple:{bg:[.07,.05,.12],room:[.022,.021,.026],glow:[.07,.015,.32],sky:[[.10,.09,.12],[.62,.62,.68]],floor:[.17,.155,.21],text:'#bfb2ea'},
          grey:{bg:[.067,.067,.067],room:[.022,.022,.022],glow:[0,0,0],sky:[[.10,.10,.10],[.64,.64,.64]],floor:[.17,.17,.17],text:'#bdbdbd'},
          white:{bg:[.949,.949,.949],room:[.42,.42,.42],glow:[0,0,0],sky:[[.42,.42,.42],[.95,.95,.95]],floor:[.86,.86,.86],text:'#4300AD'}};
        let theme='purple';try{const t=localStorage.getItem('pv-theme');if(THEMES[t])theme=t;}catch(e){}
        function setTheme(t){theme=t;root.querySelectorAll('[data-theme]').forEach(x=>x.classList.toggle('on',x.dataset.theme===t));
          canvas.style.background='rgb('+THEMES[t].bg.map(v=>Math.round(v*255)).join(',')+')';try{localStorage.setItem('pv-theme',t);}catch(e){}draw();}
        root.querySelectorAll('[data-theme]').forEach(b=>b.onclick=()=>setTheme(b.dataset.theme));
        let ready=false;
        function draw(){if(!ready||pending)return;pending=true;requestAnimationFrame(()=>{pending=false;render();});}
        function render(){
          const dpr=Math.min(window.devicePixelRatio||1,2),w=canvas.clientWidth*dpr|0,h=canvas.clientHeight*dpr|0;
          if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
          gl.viewport(0,0,w,h);const th=THEMES[theme];gl.clearColor(th.bg[0],th.bg[1],th.bg[2],1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);gl.disable(gl.CULL_FACE);
          const c=cars[car],e=eye(),inCar=mode==='car'&&!!c.body,wheelSpace=cam.space==='wheel';
          const model=wheelSpace?c.toWheel:I4,list=(inCar?c.body:[]).concat(c[side]);
          updateShadow(list,model,inCar?c.box:(c.wbox||c.box),[car,side,mode,cam.space].join('|'));
          gl.viewport(0,0,w,h);
          gl.useProgram(prog);gl.uniform1i(u('uStudio'),inCar?1:0);
          gl.uniform3fv(u('uRoom'),th.room);gl.uniform3fv(u('uGlow'),th.glow);gl.uniform3fv(u('uSkyLo'),th.sky[0]);gl.uniform3fv(u('uSkyHi'),th.sky[1]);gl.uniform3fv(u('uFloor'),th.floor);
          msg.style.color=th.text;
          gl.uniformMatrix4fv(u('uLightVP'),false,lightVP);gl.uniform1i(u('uHasShadow'),shadowOk?1:0);
          gl.activeTexture(gl.TEXTURE7);gl.bindTexture(gl.TEXTURE_2D,shadowTex);gl.bindSampler(7,null);gl.uniform1i(u('uShadow'),7);
          const near=wheelSpace?.01:.05,far=wheelSpace?(inCar?40:10):120;
          gl.uniformMatrix4fv(u('uVP'),false,mul(persp(cam.fov*Math.PI/180,w/h,near,far),look(e,target,[0,1,0])));gl.uniform3fv(u('uEye'),e);
          gl.uniformMatrix4fv(u('uModel'),false,model);
          gl.disable(gl.BLEND);gl.depthMask(true);drawList(list,false);
          gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(false);
          if(inCar&&!wheelSpace){const b=c.box;gl.uniform1i(u('uKind'),9);
            gl.uniformMatrix4fv(u('uModel'),false,[1,0,0,0,0,1,0,0,0,0,1,0,0,b[1]+.004,0,1]);
            gl.uniform4fv(u('uP0'),[(b[0]+b[3])/2,(b[2]+b[5])/2,(b[3]-b[0])/2,(b[5]-b[2])/2]);gl.bindVertexArray(groundVao());gl.drawElements(gl.TRIANGLES,6,gl.UNSIGNED_SHORT,0);
            gl.uniformMatrix4fv(u('uModel'),false,model);}
          drawList(list,true);
          gl.depthMask(true);gl.disable(gl.BLEND);gl.bindVertexArray(null);
          msg.textContent=c[side==='after'?'afterName':'beforeName']+(missing.size?' · this browser cannot show '+[...missing].join('/')+' textures':'');}
        // controls
        const viewBar=root.querySelector('.pv-views');
        function setMode(m){mode=m;if(!ready){root.querySelectorAll('[data-mode]').forEach(x=>x.classList.toggle('on',x.dataset.mode===m));return;}root.querySelectorAll('[data-mode]').forEach(x=>x.classList.toggle('on',x.dataset.mode===m));
          const vs=VIEWS[m],first=m==='car'?3:0;viewBar.innerHTML='';
          vs.forEach(([k,label,v],i)=>{const b=document.createElement('button');b.textContent=label;b.dataset.view=k;if(i===first)b.classList.add('on');
            b.onclick=()=>{home(v);viewBar.querySelectorAll('button').forEach(x=>x.classList.toggle('on',x===b));draw();};viewBar.appendChild(b);});
          home(vs[first][2]);draw();}
        let drag=null;
        canvas.addEventListener('contextmenu',e=>e.preventDefault());
        canvas.addEventListener('dblclick',()=>{const b=viewBar.querySelector('button.on');if(b&&ready)b.click();});
        canvas.addEventListener('pointerdown',e=>{drag={x:e.clientX,y:e.clientY,b:e.button};canvas.setPointerCapture(e.pointerId);});
        canvas.addEventListener('pointerup',()=>drag=null);
        canvas.addEventListener('pointermove',e=>{if(!drag||!ready)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;drag.x=e.clientX;drag.y=e.clientY;
          if(drag.b===2||e.shiftKey){const k=cam.dist*.0016;target[0]+=dx*k*Math.cos(cam.yaw);target[2]-=dx*k*Math.sin(cam.yaw);target[1]+=dy*k;}
          else{cam.yaw-=dx*.008;cam.pitch=Math.max(-1.5,Math.min(1.5,cam.pitch+dy*.008));}draw();});
        canvas.addEventListener('wheel',e=>{e.preventDefault();if(!ready)return;const [lo,hi]=cam.space==='car'?[1.2,30]:[.12,3];cam.dist=Math.max(lo,Math.min(hi,cam.dist*Math.exp(e.deltaY*.0012)));draw();},{passive:false});
        root.querySelectorAll('[data-mode]').forEach(b=>b.onclick=()=>setMode(b.dataset.mode));
        root.querySelectorAll('[data-side]').forEach(b=>b.onclick=()=>{side=b.dataset.side;root.querySelectorAll('[data-side]').forEach(x=>x.classList.toggle('on',x===b));draw();});
        const sel=root.querySelector('.pv-car');if(sel)sel.onchange=()=>{car=+sel.value;draw();};
        if(typeof DecompressionStream==='undefined'){msg.textContent='3D preview needs a current browser (Chrome, Edge or Firefox).';return;}
        msg.textContent='Loading 3D data…';
        Promise.all([...data.tex.map(async t=>{t.l=await Promise.all(t.l.map(inflate));}),
          ...data.geo.map(async g=>{for(const k of ['pos','nrm','tan','uv','idx'])g[k]=await inflate(g[k]);})])
          .then(()=>{ready=true;new ResizeObserver(draw).observe(canvas);setTheme(theme);setMode(mode);})
          .catch(e=>{msg.textContent='3D preview could not load its data: '+e;});}
        """;
}
