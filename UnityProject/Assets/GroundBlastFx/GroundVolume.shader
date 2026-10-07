Shader "GroundBlastFx/GroundVolume"
{
    // Volume de poussière / vapeur / embruns par foyer.
    // Passe 0 : raymarching à résolution réduite dans la GRILLE SIMULÉE ANCRÉE AU SOL (VolumeField.compute) :
    //           le nuage déjà formé reste là où il est né, le jet ne fait que l'alimenter et le pousser.
    //           Détail : bruit Perlin-Worley érodant, transporté par l'écoulement moyen (deux phases, sans boucle).
    //           Éclairage : ombre marchée dans la grille, Henyey-Greenstein double lobe, diffusion multiple,
    //           ciel/sol ambiants, flamme, perspective aérienne. Vide : nappe rasante (pas de grille).
    // Passe 1 : composition pleine résolution (suréchantillonnage bilatéral guidé par la profondeur)
    //           + traces au sol en espace écran.
    Properties { _GEEffectBrightness ("Luminosité des effets", Float) = 1 _MainTex ("Image", 2D) = "white" {} _GENoise ("Bruit volumique", 3D) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+100" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment fragVolume
            #include "UnityCG.cginc"
            #include "GEFlow.cginc"

            sampler2D _CameraDepthTexture;
            sampler2D _MainTex;           // image du jeu (source du Blit de la passe réduite)
            sampler3D _GENoise;
            sampler3D _GEField;
            float4 _GEFieldDims;      // x, y, z d'une grille ; nombre de grilles
            float _GESimBlend;
            float4 _GEPoints[4];      // impact.xyz, R (front ou portée de la nappe du vide)
            float4 _GENormals[4];     // normale.xyz, R_max
            float4 _GEAxes[4];        // composante tangentielle du jet.xyz, r_i
            float4 _GEParams[4];      // visibilité (ou intensité dans le vide), vapeur, surface, vide
            float4 _GEColorA[4];      // albédo clair.rgb, âge (s)
            float4 _GEColorB[4];      // albédo sombre.rgb, temps depuis la coupure (s)
            float4 _GEFlames[4];      // couleur flamme.rgb, intensité
            float4 _GEFlamePoints[4]; // tuyère.xyz, force du jet
            float4 _GEShape[4];       // R_max, dissipation 0..1, graine, portée de la nappe du vide
            float4 _GEExtra[4];       // air raréfié 0..1, intensité de la nappe en atmosphère, mode des particules, inutilisé
            float4 _GECamera, _GECamForward, _GERay00, _GERay10, _GERay01, _GERay11;
            float4 _GESunDir, _GESunColor, _GEAmbientSky, _GEAmbientGround, _GEFog;
            float _GETime;
            float _GEDebugMode;
            float _GEDetailLevel;     // 0 = Bas, 1 = Moyen, 2 = Haut/Ultra
            float _GEPixelAngle;      // angle d'un pixel de la passe réduite (rad)
            float _GEEffectBrightness; // Gain RGB ; aucune action sur la densité/profondeur.
            float _GENight;           // 0 plein jour … 1 nuit (lueur des flammes visible seulement dans l'obscurité)
            int _GECount, _GESteps, _GELightSteps, _GESurfaceGroup;
            // Taille d'un pixel de la passe réduite (m, ×4) au point marché, et poids du détail fin : servent à effacer un
            // détail plus petit que quelques pixels (crénelage en rayures au loin).
            static float gPixFoot = 1.0;
            static float gFineW = 1.0;
            // Couleur réelle du sol autour du foyer (1.8, passe 2) : rgb = albédo estimé, a = confiance ; x = poids.
            sampler2D _GEGroundCol;
            float4 _GEGroundColW[4];

            // Teinte de la poussière : palette du corps/biome mêlée à la couleur du sol réellement affiché.
            float3 GroundTint(int k, float3 palette, float scale)
            {
                float4 g = tex2Dlod(_GEGroundCol, float4((k + 0.5) / 4.0, 0.5, 0, 0));
                return lerp(palette, saturate(g.rgb * scale), saturate(g.a) * _GEGroundColW[k].x);
            }

            float3 RayAt(float2 uv)
            {
                float3 a = lerp(_GERay00.xyz, _GERay10.xyz, uv.x);
                float3 b = lerp(_GERay01.xyz, _GERay11.xyz, uv.x);
                return normalize(lerp(a, b, uv.y));
            }

            // Canaux du bruit 128³ périodique : r = Perlin-Worley, g = Worley basse fréquence,
            // b = Worley haute fréquence, a = Perlin.
            float4 N4(float3 p) { return tex3Dlod(_GENoise, float4(p, 0)); }
            float Remap(float v, float a, float b, float c, float d) { return c + (v - a) / max(b - a, 1e-4) * (d - c); }

            float HG(float mu, float g)
            {
                float gg = g * g;
                return (1 - gg) / (12.566371 * pow(max(1 + gg - 2 * g * mu, 1e-3), 1.5));
            }

            void Basis(float3 n, out float3 bx, out float3 bz)
            {
                bx = abs(n.y) < 0.9 ? normalize(cross(n, float3(0, 1, 0))) : normalize(cross(n, float3(0, 0, 1)));
                bz = cross(n, bx);
            }

            // Densité simulée (0..4) au point local p du foyer k ; 0 hors de la grille.
            float Field(int k, float3 p)
            {
                float3 uvw = GEGridUv(k, p);
                if (any(uvw <= 0.0) || any(uvw >= 1.0)) return 0;
                float zc = clamp(uvw.z, 0.5 / _GEFieldDims.z, 1.0 - 0.5 / _GEFieldDims.z);
                // 1.8 : densité estompée près des bords et du plafond de la grille. Un nuage qui y arrive (vent, jets longs)
                // n'y est plus coupé en ligne droite : le bruit des bourgeons découpe un bord irrégulier dans ce fondu.
                float2 ed = min(uvw.xz, 1.0 - uvw.xz);
                float fade = smoothstep(0.0, 0.07, min(ed.x, ed.y)) * smoothstep(0.0, 0.1, 1.0 - uvw.y);
                return tex3Dlod(_GEField, float4(uvw.x, uvw.y, (k + zc) / _GEFieldDims.w, 0)).r * fade;
            }

            // Bourgeonnements (références : nuages des décollages de Starship et Falcon 9) : gros lobes ronds en
            // chou-fleur (Worley inversé, ≈ R_max / 8) qui montent lentement, lobes moyens Perlin-Worley (≈ R_max / 25)
            // et érosion fine des bords. Le détail est transporté par l'écoulement, mais lentement : sinon le
            // cisaillement l'étire en filaments. Deux phases décalées, chaque cycle a sa propre graine (pas de boucle).
            void Billows(int k, float3 p, float Rm, out float big, out float fine, out float3 vOut)
            {
                float3 v = GEMeanVelocityEx(k, p, false);
                float vcap = clamp(0.025 * Rm, 1.5, 5.0);
                float sp = length(v);
                v *= sp > vcap ? vcap / sp : 1.0;
                vOut = v;
                float period = 2.8;
                float t = _GETime / period;
                float sLobe = 1.0 / clamp(Rm * 0.5, 8.0, 250.0);
                float sMid = 1.0 / clamp(Rm * 0.16, 3.0, 80.0);
                float seed = _GEMisc[k].x;
                bool waterK = _GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5;
                big = 0; fine = 0;
                float3 warpD = _GEDetailLevel > 0.5 ? (N4(p * sMid * 0.37 + seed + float3(0, _GETime * 0.012, 0)).a - 0.5) * 0.5 : 0;
                [unroll] for (int ph = 0; ph < 2; ph++)
                {
                    float phase = frac(t + ph * 0.5);
                    float cycle = floor(t + ph * 0.5);
                    float w = 1 - abs(2 * phase - 1);
                    float3 q = (p - v * (phase * period)) * sMid + float3(cycle * 0.371, cycle * 0.113, seed + cycle * 0.217);
                    q += warpD;
                    float4 a = N4(q);
                    // Gros lobes transportés par l'écoulement local comme le reste (plus de défilement uniforme vers le
                    // haut, qui faisait glisser tout le nuage comme une image).
                    // (Pas sur l'eau : la forme des embruns vient de leur fragmentation, voir DensityFull.)
                    float lobe = _GEDetailLevel > 0.5 && !waterK ? N4((p - v * (phase * period)) * sLobe + float3(seed + cycle * 0.29, cycle * 0.17, 0.37)).g : a.g;
                    if (_GEDetailLevel > 1.5)
                    {
                        float4 b = N4(q * 2.37 + 0.29);
                        // b (≈ 2 m) s'efface avec la distance comme le détail fin (sinon crénelage en rayures au loin).
                        big += w * (lobe * 0.45 + a.r * 0.35 + lerp(a.r, b.r, gFineW) * 0.20);
                        fine += w * (b.b * 0.7 + a.b * 0.3);
                    }
                    else if (_GEDetailLevel > 0.5)
                    {
                        big += w * (lobe * 0.5 + a.r * 0.5);
                        fine += w * a.b;
                    }
                    else
                    {
                        big += w * a.r;   // Bas : sans lecture des gros lobes (budget 0,5 ms)
                        fine += w * a.b;
                    }
                }
            }

            // Lueur du départ des jets de bouche (feu qui sort de la tranchée avec la vapeur), seulement pendant le tir.
            // Calculée à part (une variable globale écrite dans JetDensity faisait échouer la validation DX11).
            float JetGlow(int k, float3 p)
            {
                float4 jx = _GEJetX[k];
                if (jx.y < 0.003 || _GEJet[k].w < 0.5) return 0;
                float s0, r0, l0, d0, s1, r1, l1, d1;
                float a0 = GEJetShape(k, 0, p, s0, r0, l0, d0);
                float a1 = GEJetShape(k, 1, p, s1, r1, l1, d1);
                // Lueur limitée à la lèvre des bouches : au-delà, c'est de la vapeur éclairée,
                // pas un cylindre incandescent sur plusieurs dizaines de mètres.
                float g0 = a0 * saturate(1.0 - s0 / max(0.018 * jx.x, 1.5));
                float g1 = a1 * saturate(1.0 - s1 / max(0.018 * jx.x, 1.5));
                return max(g0, g1) * jx.y * saturate(_GEFlowX[k].y / 40.0);
            }

            // Forme maximale des deux jets de bouche au point p (sans bruit) : saut des zones vides et ombres.
            float JetShapeMax(int k, float3 p)
            {
                if (_GEJetX[k].y < 0.003) return 0;
                float s, rr, lat, dy;
                return max(GEJetShape(k, 0, p, s, rr, lat, dy), GEJetShape(k, 1, p, s, rr, lat, dy));
            }

            // Jet de vapeur d'une bouche du pas de tir (pas de tir de KSP 2, mods de panaches volumétriques, vidéos) :
            // cœur dense et blanc, bord sculpté en bourgeons. Le bruit est pris dans le repère du jet (xi = intégrale de ds/r,
            // écarts divisés par r) : les tourbillons grossissent avec la largeur du cône et s'écoulent vers l'extérieur à la
            // vitesse du jet (deux phases, sans boucle). Rien ne glisse d'un bloc.
            float JetDensity(int k, float3 p, float fineW)
            {
                float4 jx = _GEJetX[k];
                if (jx.y < 0.003) return 0;
                float s0, r0, l0, d0, s1, r1, l1, d1;
                float a0 = GEJetShape(k, 0, p, s0, r0, l0, d0);
                float a1 = GEJetShape(k, 1, p, s1, r1, l1, d1);
                bool first = a0 >= a1;
                float a = first ? a0 : a1;
                if (a < 0.01) return 0;
                float s = first ? s0 : s1;
                float4 o = _GEOutlets[2 * k + (first ? 0 : 1)];
                float2 dj = GEOutletDirection(k, first ? 0 : 1, o);
                float3 D = float3(dj.x, 0.0, dj.y);
                float4 fx = _GEFlowX[k];
                float bo = max(fx.z, 1.0);
                // Vitesse visuelle quasi constante le long du jet (plafonnée) : peu de cisaillement, pas d'étirement.
                // Jets qui fusent (1.7) : défilement plus rapide, période plus courte (même étirement par cycle).
                float puffJet = saturate(_GEJetMotion[k].y);
                float speed = lerp(fx.y, _GEJetMotion[k].x, puffJet);
                float uVis = min(speed * (5.0 * bo) / (5.0 * bo + max(s, 0.0)), 85.0) + 2.0;
                // Taille des bourgeons ≈ rayon du jet à mi-longueur (échelle fixe par jet : aucun motif en rayons).
                float rMid = jx.z + GE_JET_OPENING * 0.5 * jx.x;
                float scale = 1.0 / max(rMid * 1.1, 3.0);
                const float period = 0.75;
                float t = _GETime / period;
                float seed = _GEMisc[k].x + (first ? 0.0 : 0.53);
                float big = 0, fine = 0;
                [unroll] for (int ph = 0; ph < 2; ph++)
                {
                    float phase = frac(t + ph * 0.5);
                    float cycle = floor(t + ph * 0.5);
                    float w = 1 - abs(2 * phase - 1);
                    float3 q = (p - D * (uVis * phase * period)) * scale
                             + float3(cycle * 0.371 + seed, cycle * 0.113, cycle * 0.217);
                    float4 n = N4(q);
                    big += w * (n.r * 0.55 + n.g * 0.45);
                    fine += w * (_GEDetailLevel > 0.5 ? N4(q * 2.1 + 0.29).b : n.b);
                }
                // Le front de coupure est déjà appliqué localement par GEJetShape. Affaiblir tout le jet
                // selon jx.w effaçait aussi la vapeur en aval avant que la coupure ne l'atteigne.
                float detach = (1.0 - puffJet) * saturate(jx.w / max(0.08 * jx.x, 6.0));
                float live = smoothstep(0.2, 0.85, jx.y) * (1.0 - detach);
                float d = saturate(Remap(big, saturate(1.0 - a * 1.2 * live), 1.0, 0.0, 1.0));
                // Détail fin propre aux jets (≈ rMid / 12) : effacé quand il passe sous ~4 pixels (sinon rayures à distance).
                float fineJ = saturate((rMid * 1.1 / 25.0) / max(gPixFoot, 1e-3) - 0.25);
                d = saturate(Remap(d, fine * 0.14 * fineJ, 1.0, 0.0, 1.0));
                // Densité propre au jet (l'extinction du nuage est réduite pour les très gros nuages ; pas celle d'un jet dense).
                // Opaque par son épaisseur (≈ 0,15 /m), pas par une densité extrême : sinon chaque pas de marche passe d'un
                // coup du transparent à l'opaque et le tramage se voit (texture « tissée »).
                // Plancher de densité (volume plein des jets) seulement quand le jet est alimenté : en s'éteignant, il ne
                // reste que les bouffées (1.9.2 : le plancher fixe laissait un tube translucide lisse après le départ).
                float broken = smoothstep(0.20, 0.62, big);
                float jd = smoothstep(0.03, 0.35, d) * saturate(a * 2.8) * pow(jx.y, 0.75) * lerp(lerp(0.05, 0.68, live), 1.0, broken) * (1.0 - 0.5 * detach);
                return jd * 1.15 / saturate(90.0 / max(_GEFlowP[k].z, 4.0) + 0.75);
            }

            // fineW : poids du détail fin (1 de près, 0 quand il devient plus petit qu'un pixel : pas de « fourrure »).
            float DensityFull(int k, float3 p, float Rm, float fineW, out float heightFrac)
            {
                heightFrac = saturate((p.y - GEGroundHeight(k, p.xz)) / max(0.45 * Rm, 10.0));
                // Lecture de la grille déformée par un bruit basse fréquence (~1,5 maille) : la surface du nuage ne
                // trahit jamais les mailles de la grille (pas de facettes, pas de faces planes).
                float cell = 2.0 * max(_GEGridO[k].w, 1.0) / max(_GEFieldDims.x, 1.0);
                float3 wq = p / max(cell * 5.0, 1.0) + _GEMisc[k].x + float3(0, _GETime * 0.05, 0);
                float3 warp = (N4(wq).rga - 0.5) * (3.0 * cell);
                float f = Field(k, p + warp);
                if (f < 0.01) return _GEDebugMode > 0.5 ? 0 : JetDensity(k, p, fineW);
                bool water = _GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5;
                // Bande de transition large (plusieurs mailles) où le bruit sculpte les bourgeonnements.
                // Les parties très diluées de l'embrun ne doivent pas former une coupole opaque et lointaine.
                // Vapeur de pas de tir : reste visible même diluée (les nuages de lancement persistent des minutes).
                float coverage = water ? smoothstep(0.03, 1.2, f) : smoothstep(0.0, lerp(1.6, 0.6, _GEJet[k].z), f);
                if (_GEDebugMode > 0.5) return coverage;
                float steam = _GEJet[k].z;
                float big, fine;
                float3 vb;
                Billows(k, p, Rm, big, fine, vb);
                // Couverture appliquée au bruit : au cœur presque tout passe, au bord seules les bosses les plus
                // hautes survivent → contour en choux-fleurs, jamais une surface lisse.
                float d = saturate(Remap(big, 1.0 - coverage * 0.92, 1.0, 0.0, 1.0));
                float diss = _GEShape[k].y;
                // Détail effiloché à la base, en bourgeons arrondis au-dessus (Schneider, Horizon Zero Dawn ; même principe
                // dans les nuages volumétriques de blackrack) : Worley à la base, Worley inversé plus haut.
                // (Vapeur seulement : sur la poussière, le Worley inversé donnait des bords « poilus ».)
                float fineShape = lerp(fine, 1.0 - fine, saturate(heightFrac * 4.0) * steam);
                d = saturate(Remap(d, fineShape * (lerp(0.22, 0.24, steam) + 0.3 * diss) * fineW, 1.0, 0.0, 1.0));
                // Les régions minces restent minces : le bruit sculpte la forme mais n'amplifie pas une brume diffuse
                // en paquets opaques (densité moyenne ≈ couverture, comme dans un vrai nuage).
                // Vapeur : bords nets de condensation (les photos montrent des contours francs) ; poussière plus diffuse.
                // Embruns au-dessus de l'eau : contours doux, comme la poussière.
                // Embruns chauds (jet de fusée sur l'eau : vapeur + gouttelettes, comme les amerrissages de Starship) :
                // nuage bouillonnant aux contours assez nets, pas une brume trouée.
                float crisp = steam * (water ? 0.6 : 1.0);
                float density = smoothstep(0.02 + 0.02 * crisp, lerp(0.45, 0.30, crisp), d) * saturate(coverage * 4.0);
                // Voile de fond proportionnel au champ simulé : un nuage qui se dilue s'estompe en brume (comme un vrai
                // nuage de vapeur) au lieu de disparaître d'un coup quand il passe sous le seuil des bourgeonnements.
                // Seulement pendant la dissipation (bourgeons nets pendant le tir).
                // Vapeur : un voile léger en permanence (un nuage qui s'étale s'estompe en brume, il ne disparaît pas).
                bool padSite = _GEMisc[k].y > 1.5 && _GEMisc[k].y < 2.5;
                // 1.8.1 : voile découpé par les bourgeons (lambeaux) : lisse, il faisait une nappe floue sans relief là où la
                // vapeur est très diluée (traînée laissée par les jets du pas, « retour » au-dessus du nuage vu de haut).
                // Et rien là où la vapeur est presque nulle : vue en rasant sur des centaines de mètres, même une trace infime
                // s'accumulait en voile lisse (le bruit s'y moyenne).
                float wisps = smoothstep(0.25, 0.6, big) * (0.75 + 0.25 * fine) * smoothstep(0.04, 0.2, coverage);
                // 1.9.3 : sur un pas de tir, voile réduit (0,06 au lieu de 0,16, brume de dissipation divisée par deux) : les restes
                // formaient des nappes de brume plates et lisses qui duraient plusieurs minutes.
                density = max(density, coverage * wisps * max(lerp(0.2, 0.32, steam) * saturate(diss * 4.0) * (padSite ? 0.5 : 1.0), padSite ? 0.06 * steam : 0.0));
                if (water)
                {
                    // Embruns en bouffées : lacunes et filaments animés, sans disque blanc uniforme.
                    // La modulation suit le vent et la turbulence, tandis que la densité simulée reste ancrée au sol.
                    // Transportées par l'écoulement local (deux phases) : pas de défilement d'un bloc.
                    float3 vw = vb;   // vitesse déjà calculée (et plafonnée) pour les bourgeons
                    float tw = _GETime / 1.6;
                    // Valeurs de bruit mélangées AVANT le seuil : les trous restent francs (sinon le mélange des deux
                    // phases rend la brume uniforme, plus dense et plus chère à éclairer).
                    float breakupN = 0;
                    [unroll] for (int ph = 0; ph < 2; ph++)
                    {
                        float phase = frac(tw + ph * 0.5);
                        float cycle = floor(tw + ph * 0.5);
                        float w = 1 - abs(2 * phase - 1);
                        // Une lecture : Worley basse fréquence (g) + Perlin-Worley (r) du même texel.
                        float4 patches = N4((p - vw * (phase * 1.6)) / 6.0 + float3(_GEMisc[k].x + cycle * 0.371, cycle * 0.113, 0));
                        // 1.8 : moins de Worley (trous ronds « taches de léopard » vus de haut), plus de Perlin (lacunes effilées).
                        breakupN += w * (patches.a * 0.45 + patches.g * 0.3 + patches.r * 0.25);
                    }
                    // Lacunes seulement sur les bords (couverture faible) ; le cœur du nuage d'embruns reste dense.
                    float breakup = smoothstep(0.40, 0.62, breakupN);
                    density *= lerp(0.55 + 0.45 * breakup, 1.0, saturate(coverage * 1.6 - 0.3));
                }
                else
                {
                    // 1.7 : l'opacité accrue vaut pour le cœur du nuage ; ses restes dilués gardent la translucidité de la 1.6
                    // (sinon, en se dissipant, le nuage laisse de petites bouffées sombres qui flottent en l'air).
                    density *= lerp(0.55, 1.0, saturate(f * 2.0));
                }
                float jetD = JetDensity(k, p, fineW);
                // Le jet cède aux bouffées là où leur champ devient dense. Même nuage, sans double
                // texture ni somme de vapeur dans le chevauchement ; aucune action hors du jet.
                float puffJet = saturate(_GEJetMotion[k].y);
                jetD *= 1.0 - puffJet * smoothstep(0.08, 0.65, f);
                return max(density, jetD) + 0.3 * min(density, jetD) * (1.0 - puffJet);
            }

            float Extinction(int k, float Rm)
            {
                // Nuages opaques à quelques dizaines de mètres (on ne voit pas à travers un nuage de lancement).
                // Embruns : un peu moins denses que la vapeur d'un pas de tir (1.6 : 0,065 → 0,11).
                // 1.7 : encore plus opaques (retour en jeu : « toujours un peu transparentes »).
                float base = lerp(0.2, 0.28, _GEJet[k].z);
                if (_GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5) base = 0.16;
                return base * saturate(90.0 / Rm + 0.75);
            }

            // Marche dans la grille ancrée du foyer k. Renvoie la lumière prémultipliée et l'opacité ; tEnter = distance d'entrée.
            // Distance d'un point au segment [a, b].
            float DistSeg(float3 p, float3 a, float3 b)
            {
                float3 ab = b - a;
                float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6));
                return length(p - a - ab * t);
            }

            float4 MarchGrid(int k, float3 ray, float sceneDistance, float jitter, out float tEnter)
            {
                tEnter = 1e9;
                float vis = _GEParams[k].x;
                float3 east, up, north;
                GEBasis(k, east, up, north);
                float E = max(_GEGridO[k].w, 1.0), Hg = max(_GEGridU[k].w, 1.0);
                float3 o = _GECamera.xyz - _GEGridO[k].xyz;
                float3 lo = float3(dot(o, east), dot(o, up), dot(o, north));
                float3 ld = float3(dot(ray, east), dot(ray, up), dot(ray, north));
                float3 bmin = float3(-E, -1.0, -E), bmax = float3(E, Hg, E);
                float3 inv = 1.0 / (abs(ld) > 1e-6 ? ld : (ld >= 0 ? 1e-6 : -1e-6));
                float3 ta = (bmin - lo) * inv, tb = (bmax - lo) * inv;
                float3 tmin = min(ta, tb), tmax = max(ta, tb);
                float enter = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                float leave = min(min(min(tmax.x, tmax.y), tmax.z), sceneDistance);
                if (leave <= enter) return 0;
                tEnter = enter;

                float Rm = max(_GEFlowP[k].z, 4.0);
                float cell = 2.0 * E / max(_GEFieldDims.x, 1.0);
                float stepMul = _GEDetailLevel > 1.5 ? (_GESteps > 80 ? 0.55 : 0.8) : (_GEDetailLevel > 0.5 ? 1.55 : 2.1);
                int steps = (int)clamp(ceil((leave - enter) / (cell * stepMul)), 8, max(_GESteps, 8));
                float stepLen = (leave - enter) / steps;
                float sigma = Extinction(k, Rm) * vis;
                float steam = _GEJet[k].z;
                float3 sunDir = _GESunDir.xyz;
                float3 sunL = float3(dot(sunDir, east), dot(sunDir, up), dot(sunDir, north));
                float mu = dot(ray, sunDir);
                float gF = lerp(0.60, 0.80, steam), gB = lerp(-0.25, -0.30, steam);
                float3 albA = GroundTint(k, _GEColorA[k].rgb, 1.08), albB = GroundTint(k, _GEColorB[k].rgb, 0.78);
                float3 steamAlb = float3(0.84, 0.85, 0.87);
                float3 flameCol = _GEFlames[k].rgb * _GEFlames[k].a;
                // Photos de décollages et d'atterrissages : même en plein jour, la flamme éclaire nettement le bas du
                // nuage (jaune-orangé) ; la nuit, c'est la seule lumière.
                // De jour, le soleil domine : lueur discrète (sinon tout le nuage vire au jaune-orange) ; forte la nuit.
                // 1.7 : de jour aussi (photos de décollages : le pied du nuage est orange en plein jour), mais seulement
                // près de la flamme (pas de traîne qui jaunit tout le nuage) ; la nuit, forte et plus étendue.
                flameCol = _GEFlames[k].rgb * min(_GEFlames[k].a, 1.2) * lerp(0.4, 1.0, _GENight);
                float3 flameP = _GEFlamePoints[k].xyz;
                // Portée de la lueur : le bas du nuage près de la flamme, pas tout le nuage (photos de jour).
                float flameRange = (min(max(_GESrc[k].w * 5.0, Rm * 0.18), Rm * 0.35) + 5.0) * lerp(1.0, 2.2, _GENight);
                // 1.0.2 : feu du pas à la Juno: New Origins. Sur un pas de tir, le pied du nuage est éclairé de l'intérieur par
                // toute la partie chaude de la flamme (de la tuyère vers le sol) et par le feu qui sort des bouches de la
                // tranchée : crème-jaune près du feu, orange plus loin, net même de jour ; de nuit, tout le nuage rougeoie.
                bool padFire = _GEMisc[k].y > 1.5 && _GEMisc[k].y < 2.5;
                float fireAmt = padFire ? saturate(_GEFlames[k].a) : 0.0;
                float3 fireA = 0, fireB = 0, out0 = 0, out1 = 0, dir0 = 0, dir1 = 0, fireTint = 1;
                float fireR = 1, jetFireL = 0, w0 = 0, w1 = 0;
                if (fireAmt > 0.001)
                {
                    float3 relF = flameP - _GEGridO[k].xyz;
                    fireA = float3(dot(relF, east), dot(relF, up), dot(relF, north));          // tuyère, repère de la grille
                    float3 toImpact = _GESrc[k].xyz - fireA;
                    float lenI = length(toImpact);
                    float hotLen = clamp(Rm * 0.25, 30.0, 150.0);                            // partie chaude de la flamme
                    fireB = fireA + toImpact * (min(lenI, hotLen) / max(lenI, 1e-3));
                    fireR = clamp(Rm * 0.3, 20.0, 160.0) * lerp(1.0, 2.0, _GENight);
                    float4 o0 = _GEOutlets[2 * k], o1 = _GEOutlets[2 * k + 1];
                    out0 = float3(o0.x, o0.z, o0.y); out1 = float3(o1.x, o1.z, o1.y);
                    float2 od0 = GEOutletDirection(k, 0, o0), od1 = GEOutletDirection(k, 1, o1);
                    dir0 = float3(od0.x, 0, od0.y); dir1 = float3(od1.x, 0, od1.y);
                    w0 = o0.w > 0.01 ? 1.0 : 0.0; w1 = o1.w > 0.01 ? 1.0 : 0.0;
                    // Feu à la sortie des bouches tant que le jet souffle : sur ~0,35 de la portée des bouffées (la moitié de
                    // la longueur de jet du nuage), indépendamment du jet visible, raccourci en langue de feu.
                    jetFireL = 0.35 * 0.5 * clamp(1.45 * Rm, 50.0, 400.0) * saturate(_GEJetX[k].y * 2.0);
                    float3 prop = _GEFlames[k].rgb / max(max(_GEFlames[k].r, _GEFlames[k].g), max(_GEFlames[k].b, 1e-3));
                    fireTint = lerp(1.0, prop, 0.5);   // la couleur de l'ergol nuance le feu (kérolox plus orange)
                }
                float lightStep = max(cell * 1.2, 1.0);
                float fineWave = clamp(Rm * 0.11, 2.5, 60.0) / 9.5; // longueur d'onde du détail fin (m)
                bool waterMarch = _GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5;
                float3 bounce = _GESunColor.rgb * saturate(sunL.y) * albA * (waterMarch ? 0.05 : 0.5);

                float3 accum = 0;
                float trans = 1;
                // Saut des zones vides : une lecture brute de la grille (1 accès) suffit à savoir qu'il n'y a rien ;
                // on avance alors de 2 pas, et on recule d'un pas à la première rencontre de matière.
                // Densité modérée des jets (1.6) : des pas d'un tiers de leur rayon suffisent (plus de tramage visible).
                float jetStep = max(2.0, _GEJetX[k].z * (_GESteps > 80 ? 0.33 : _GEDetailLevel > 1.5 ? 0.45 : 0.7));
                float t = enter + jitter * stepLen;
                bool skipping = false;
                bool hasJets = _GEJetX[k].y > 0.003;
                int maxIter = hasJets && _GESteps > 80 ? 200 : 160;   // Ultra : pas raccourcis dans les jets, plus d'itérations
                // 1.7 : bord visible du nuage échantillonné à pas fins. Avec des pas plus longs que le détail fin, le bord
                // est échantillonné en tranches que le tramage décale d'un pixel à l'autre : texture tissée « en écailles »
                // sur les gros nuages du pas de tir. À la première rencontre de matière, on revient à l'échantillon
                // précédent et on avance à pas fins tant que le bord reste visible (quelques pas, coût borné ;
                // moins en Bas et Moyen pour tenir leurs budgets).
                float fineStep = max(fineWave * 0.9, 0.8);
                int fineCount = _GESteps > 80 ? 16 : (_GEDetailLevel > 1.5 ? 12 : (_GEDetailLevel > 0.5 ? 4 : 3));
                int fineLeft = 0, refines = 0;
                float tPrev = t, dPrev = 0;
                [loop] for (int s = 0; s < 200; s++)
                {
                    if (s >= maxIter || t >= leave || trans < 0.01) break;
                    float3 p = lo + ld * t;
                    float jetHere = 0;
                    [branch] if (hasJets) jetHere = JetShapeMax(k, p);
                    // Dans un jet de bouche (bien plus fin que les mailles d'une grande grille), pas raccourci :
                    // sinon 2 ou 3 échantillons par jet donnent une texture « tissée » et translucide.
                    float stepHere = jetHere > 0.01 ? min(stepLen, jetStep) : stepLen;
                    if (fineLeft > 0) stepHere = min(stepHere, fineStep);
                    if (Field(k, p) < 0.004 && jetHere < 0.01)
                    {
                        t += 2.0 * stepLen;
                        skipping = true;
                        fineLeft = 0; dPrev = 0;
                        continue;
                    }
                    if (skipping)
                    {
                        skipping = false;
                        t = max(t - (jetHere > 0.01 ? stepHere : stepLen), enter);
                        p = lo + ld * t;
                        tPrev = t;
                    }
                    float hf;
                    // Au moins ~4 pixels (passe réduite) par longueur d'onde du détail fin, sinon crénelage en fibres.
                    gPixFoot = 8.0 * t * _GEPixelAngle;
                    // Détail fin : au moins ~6 pixels (passe réduite) par longueur d'onde, sinon rayures et fourmillement.
                    float fineW = saturate(fineWave / max(1.5 * gPixFoot, 1e-3) - 0.25);
                    gFineW = fineW;
                    float d = DensityFull(k, p, Rm, fineW, hf);
                    float tHere = t;
                    if (fineLeft == 0 && refines < 2 && d >= 0.02 && dPrev < 0.02 && trans > 0.3 && tHere - tPrev > 1.5 * fineStep)
                    {
                        refines++;
                        fineLeft = fineCount;
                        t = tPrev + fineStep;
                        dPrev = 0;
                        continue;
                    }
                    if (fineLeft > 0) fineLeft--;
                    if (trans <= 0.3) fineLeft = 0;
                    tPrev = tHere; dPrev = d;
                    t += stepHere;
                    if (d < 0.003) continue;
                    // 1.7 : dans la matière dense, pas raccourci (≤ 0,5 d'épaisseur optique par pas). Sinon, avec de grandes
                    // mailles, un seul pas passe du transparent à l'opaque : le bord du nuage suit les coquilles de la marche
                    // (texture en « écailles » sur les gros nuages du pas de tir).
                    float stepUse = min(stepHere, max(0.5 / (d * sigma), 0.25 * stepHere));
                    t = tHere + stepUse;
                    float od = d * sigma * stepUse;

                    // Ombre : marche vers le soleil DANS la grille simulée (le vrai nuage, pas une enveloppe).
                    float tau = 0;
                    float ls = lightStep;
                    float3 lp = p;
                    [loop] for (int l = 0; l < 6; l++)
                    {
                        if (l >= _GELightSteps) break;
                        lp += sunL * ls;
                        // Ombre propre des jets : deux premiers pas seulement (la grille contient déjà la vapeur de leur bout).
                        float jl = 0;
                        [branch] if (hasJets && l < 2) jl = JetShapeMax(k, lp) * _GEJetX[k].y * 1.5;
                        tau += saturate(Field(k, lp) * 1.3 + jl) * 0.85 * ls;
                        ls *= 1.7;
                    }
                    // Auto-ombrage des bourgeonnements : un échantillon détaillé tout près (Ultra).
                    if (_GESteps > 80 && d > 0.08) // Ultra : un échantillon détaillé de plus, seulement dans la matière dense
                    {
                        // Distance et densité bornées : dans une grande grille (mailles de 7 m) ou un jet dense, sinon
                        // l'échantillon tombe dans un autre bourgeon et fait des taches noires.
                        float hs;
                        float dsh = min(cell * 0.7, 4.0);
                        tau += min(DensityFull(k, p + sunL * dsh, Rm, fineW, hs), 1.2) * dsh * 2.0;
                    }
                    tau *= sigma * 0.9;
                    float3 sun = 0;
                    float a = 1, bb = 1, c = 1;
                    // Nuage épais vu dos au soleil : blanc (diffusion multiple, rétrodiffusion), pas gris.
                    float wIso = lerp(0.55, 0.50, steam);
                    // Diffusion multiple par octaves (Wrenninge) : la lumière traverse les nuages épais par diffusions
                    // successives ; un nuage de vapeur épais reste gris clair, jamais noir.
                    [unroll] for (int oc = 0; oc < 3; oc++)
                    {
                        float hg = lerp(HG(mu, gB * c), HG(mu, gF * c), 0.7);
                        float phase = lerp(hg, 0.0795775, wIso);
                        sun += a * phase * exp(-tau * bb);
                        a *= lerp(0.62, 0.75, steam); bb *= lerp(0.22, 0.15, steam); c *= 0.55;
                    }
                    // Effet « poudre » (Schneider, repris par les nuages volumétriques de blackrack) : un bord mince diffuse
                    // peu de lumière vers l'avant ; vu dos au soleil, les sillons entre bourgeonnements s'assombrissent et
                    // le nuage prend son relief en chou-fleur. Faible vu face au soleil (liseré lumineux conservé).
                    float powder = 1.0 - exp(-(d * sigma * cell * 4.0 + tau * 2.0));
                    sun *= lerp(1.0, powder, 0.2 * saturate(0.55 - 0.45 * mu));
                    // Étalonné pour une image SANS HDR (caméra de KSP) : une face au soleil ≈ 0,9 du blanc, la face à
                    // l'ombre ≈ 0,4 à 0,5 : le relief des bourgeons reste visible (en 1.5, tout saturait en blanc plat).
                    float3 lit = _GESunColor.rgb * sun * 12.566 * lerp(0.44, 0.5, steam);
                    // Ambiant : ciel désaturé (les faces à l'ombre des nuages de vapeur sont gris clair, à peine
                    // bleutées), assombri au cœur ; bases plus sombres pour la poussière, la vapeur reste claire.
                    float3 sky = lerp(_GEAmbientSky.rgb, dot(_GEAmbientSky.rgb, float3(0.3, 0.5, 0.2)).xxx, 0.45);
                    // 1.7 : soleil renvoyé par le sol (teinte de la poussière = celle du sol) : le dessous d'un nuage de
                    // neige soufflée ou de sable clair reste lumineux, pas gris sombre. L'eau renvoie peu.
                    float3 ambient = lerp(_GEAmbientGround.rgb, sky * 0.9, saturate(hf * 1.2 + 0.15))
                                     * (lerp(0.55, 0.8, steam) + lerp(0.45, 0.2, steam) * exp(-tau * 0.4))
                                   + bounce * (1.0 - 0.6 * hf) * (0.5 + 0.5 * exp(-tau * 0.3));
                    float3 world = _GEGridO[k].xyz + east * p.x + up * p.y + north * p.z;
                    float fd = length(world - flameP) / flameRange;
                    // Lueur forte au pied du nuage, qui s'éteint vite au-delà de la portée : de jour, le reste du nuage
                    // de vapeur reste blanc (photos de décollages), la nuit la portée est plus grande.
                    float3 flame = flameCol * (2.8 * exp(-1.6 * fd * fd) + 0.25 * _GENight / (1 + fd * fd * 4.0)) * (0.35 + 0.65 * exp(-d * 0.9)) * (1 - hf * 0.55);
                    if (fireAmt > 0.001)
                    {
                        float dCol = DistSeg(p, fireA, fireB);
                        float dJet = 1e6;
                        if (jetFireL > 1.0)
                        {
                            if (w0 > 0.5) dJet = min(dJet, DistSeg(p, out0, out0 + dir0 * jetFireL));
                            if (w1 > 0.5) dJet = min(dJet, DistSeg(p, out1, out1 + dir1 * jetFireL));
                        }
                        // De jour, lueur plus serrée autour du feu (au-delà, l'orange sur le blanc bleuté virait au rose) ;
                        // de nuit, elle porte loin. Cœur crème resserré : le relief des bouffées reste lisible.
                        float r2 = fireR * fireR * lerp(0.55, 1.0, _GENight);
                        float g = exp(-dCol * dCol / r2) + 0.8 * exp(-dJet * dJet / (0.35 * r2));
                        float hot = saturate(exp(-dCol * dCol / (0.06 * r2)) + 0.5 * exp(-dJet * dJet / (0.04 * r2)));
                        float3 fire = lerp(float3(1.0, 0.42, 0.1), float3(1.0, 0.84, 0.55), hot) * fireTint;
                        flame = fire * fireAmt * lerp(2.2, 2.4, _GENight) * g * (0.35 + 0.65 * exp(-d * 0.9));
                    }
                    // À la bouche le panache est chaud et orangé. La lumière de la tuyère ne doit pas
                    // saturer la vapeur en blanc dans un tube parfaitement régulier.
                    float mouthGlow = hasJets ? JetGlow(k, p) * saturate(_GEFlames[k].a) : 0.0;
                    flame *= 1.0 - 0.65 * mouthGlow;
                    float3 albedo = lerp(lerp(albB, albA, saturate(hf * 1.3 + d * 0.3)), steamAlb, steam);
                    float3 color = albedo * (lit + ambient) + albedo * flame;
                    color = lerp(color, float3(0.96, 0.38, 0.12), saturate(0.8 * mouthGlow));
                    float fog = 1 - exp(-tHere * _GEFog.w);
                    color = lerp(color, _GEFog.rgb, fog);

                    float opacity = 1 - exp(-od);
                    accum += trans * opacity * color;
                    trans *= 1 - opacity;
                }
                return float4(accum, 1 - trans);
            }

            // Régolithe en vide : une nappe balistique mince, traversée de filaments radiaux (voile de la 1.8, rétabli en
            // 1.9.2 : l'auteur le trouvait plus détaillé que les versions 1.9). Le fond reste transparent ; les structures se
            // déplacent vers l'extérieur avec les grains.
            float SheetAlpha(int k, float3 pos, float intensity)
            {
                float3 n = _GENormals[k].xyz;
                float R = max(_GEPoints[k].w, 2.0);
                float ri = max(_GEAxes[k].w, 0.3);
                float3 rel = pos - _GEPoints[k].xyz;
                rel -= n * dot(rel, n);
                float r = length(rel);
                float reach = _GEShape[k].w > 0 ? _GEShape[k].w : R;
                if (r > reach * 1.05) return 0;
                float3 bx, bz; Basis(n, bx, bz);
                float2 dirv = float2(dot(rel, bx), dot(rel, bz)) / max(r, 1e-3);
                float seed = _GEShape[k].z;
                // La direction conserve les rayons pendant que le détail avance radialement.
                // Deux échelles cassent la symétrie sans former de disques ou d'anneaux uniformes.
                float broad = N4(float3(dirv * 2.8, seed + _GETime * 0.016)).a;
                // 1.6 : bouffées larges qui filent vers l'extérieur (plus de fines lignes qui convergent vers l'impact :
                // vues en rasant, elles donnaient un effet « saut dans l'hyperespace »).
                float narrow = N4(float3(dirv * 3.5, r / max(ri * 5.0, 10.0) - _GETime * 1.1 + 0.31 + seed)).b;
                float moving = N4(float3(dirv * 2.0,
                                         r / max(ri * 4.0, 8.0) - _GETime * 1.5 + seed)).a;
                float fan = smoothstep(0.28, 0.69, broad);
                float filament = smoothstep(0.35, 0.65, moving * 0.6 + narrow * 0.4);
                float front = reach * (0.80 + 0.24 * N4(float3(dirv * 1.8, seed + _GETime * 0.025)).b);
                // 1.6 : la poussière part de tout le disque sous le jet (plus de trou de rayon r_i : à 50 m d'altitude, la
                // tache fait 60 m et la nappe n'apparaissait qu'au-delà, presque transparente). Films d'Apollo : un voile
                // dense et strié qui file vers l'extérieur et brouille le sol près de l'impact, puis s'efface au loin.
                float env = smoothstep(ri * 0.05, ri * 0.4, r) * (1 - smoothstep(front * 0.5, front, r));
                float radial = 1 - smoothstep(0.2 * reach, reach, r);
                return saturate(intensity * env * radial * (0.5 + 0.2 * fan + 0.35 * filament));
            }

            float4 VacuumSheet(int k, float3 ray, float sceneDistance, bool skyPixel, float intensity, float3 sceneCol, float3 surfN, out float tEnter)
            {
                tEnter = 1e9;
                float3 n = _GENormals[k].xyz;
                float alphaGround = 0;
                float alphaFlight = 0;
                if (!skyPixel)
                {
                    float3 surf = _GECamera.xyz + ray * sceneDistance;
                    float h = dot(surf - _GEPoints[k].xyz, n);
                    float r = length((surf - _GEPoints[k].xyz) - n * h);
                    // Seulement le sol (surface à peu près horizontale), jamais les pièces du vaisseau : le module posé
                    // disparaissait sous le voile (1.9.1). surfN : normale de l'écran, calculée hors des branches.
                    float groundLike = smoothstep(0.45, 0.75, abs(dot(surfN, n)));
                    // Voile plaqué au relief visible, y compris Parallax ; il ne doit pas cacher le sol.
                    // Tolérance large : sur un relief Parallax (pente, rochers), le sol visible s'écarte vite du plan de
                    // l'impact ; la poussière le recouvre quand même.
                    if (h > -20.0 && h < 20.0 + 0.3 * r)
                    {
                        alphaGround = 0.8 * SheetAlpha(k, surf, intensity) * groundLike;
                        if (alphaGround > 0) tEnter = sceneDistance;
                    }
                }
                // À 1–3 degrés, les grains rapides montent de quelques mètres seulement avant de
                // retomber. Une couche élevée donne une silhouette et de la parallaxe, sans fumée.
                // Intersection exacte avec h = min(pente * rayon, hauteur limite). Tester un plan
                // intermédiaire écarterait à tort les vues rasantes prises sous ce plan.
                const float slope = 0.032;
                const float capHeight = 5.0;
                float3 origin = _GECamera.xyz - _GEPoints[k].xyz;
                float originH = dot(origin, n), rayH = dot(ray, n);
                float3 originT = origin - n * originH, rayT = ray - n * rayH;
                float slope2 = slope * slope;
                float a = rayH * rayH - slope2 * dot(rayT, rayT);
                float b = 2.0 * (originH * rayH - slope2 * dot(originT, rayT));
                float c = originH * originH - slope2 * dot(originT, originT);
                float3 hits = float3(1e9, 1e9, 1e9);
                if (abs(a) < 1e-7)
                {
                    // Rayon parallèle à une génératrice : l'équation devient linéaire.
                    if (abs(b) > 1e-6) hits.x = -c / b;
                }
                else
                {
                    float discriminant = b * b - 4.0 * a * c;
                    if (discriminant >= 0)
                    {
                        // Forme stable, y compris lorsqu'une intersection est très proche de la caméra.
                        float q = -0.5 * (b + (b >= 0 ? 1.0 : -1.0) * sqrt(discriminant));
                        if (abs(q) > 1e-6) hits.xy = float2(q / a, c / q);
                        else hits.x = -b / (2.0 * a);
                    }
                }
                if (abs(rayH) > 1e-6) hits.z = (capHeight - originH) / rayH;
                [unroll] for (int hit = 0; hit < 3; hit++)
                {
                    float t = hits[hit];
                    if (t <= 0 || t >= sceneDistance + 0.25) continue;
                    if (hit == 1 && abs(t - hits.x) < 0.01) continue; // tangence : une seule couche
                    float h = originH + rayH * t;
                    float radius = length(originT + rayT * t);
                    // L'équation quadratique décrit aussi le cône inférieur, qui n'existe pas ici.
                    if (hit < 2 && (h < 0 || h >= capHeight)) continue;
                    if (hit == 2 && radius < capHeight / slope) continue;
                    // Couche en vol : discrète, et effacée tout près de la caméra (sinon, vue en rasant, ses stries
                    // radiales couvrent tout le premier plan comme un flou de zoom).
                    float alphaHit = SheetAlpha(k, _GECamera.xyz + ray * t, intensity) * 0.3 * smoothstep(1.5, 14.0, t);
                    alphaFlight = 1.0 - (1.0 - alphaFlight) * (1.0 - alphaHit);
                    if (alphaHit > 0) tEnter = min(tEnter, t);
                }
                float alpha = 1 - (1 - alphaGround) * (1 - alphaFlight);
                if (alpha <= 0) return 0;
                // Grains en plein soleil, au-dessus du sol : diffusion vers l'avant marquée.
                float mu = dot(ray, _GESunDir.xyz);
                // 1.7 : plus claire que le sol (films d'Apollo : un voile lumineux qui brouille le relief), sinon gris sur gris.
                // (Face au soleil : lueur limitée, sinon tout le sol visible devient blanc.)
                float phase = 0.55 + 0.5 * pow(saturate(mu * 0.5 + 0.5), 5);
                float3 dust = lerp(GroundTint(k, _GEColorA[k].rgb, 1.08), float3(0.84, 0.82, 0.78), 0.22);
                float3 col = saturate(dust * 1.15 + 0.08) * (_GEAmbientSky.rgb + _GEAmbientGround.rgb + _GESunColor.rgb * phase * 1.12);
                // Voile relatif au sol réellement affiché (1.7) : il l'éclaircit d'environ 35 % et en adoucit le relief,
                // quelle que soit sa teinte (sur le sol clair de la Mün, une couleur fixe se confondait avec lui).
                float3 veil = max(sceneCol * 1.35 + 0.02, col);
                return float4(lerp(col, veil, 0.7) * alpha, alpha);
            }

            // Eau brassée par le jet (vidéos d'hélicoptères, de Harrier et d'amerrissages de boosters) : écume blanche en
            // cellules et plaques, eau aérée plus claire autour. La TEXTURE est ancrée sur l'eau (repère fixe de la grille)
            // et transportée par l'écoulement de surface (deux phases, sans boucle) ; seule la zone agitée suit le jet,
            // comme la vraie zone d'impact. L'écume persiste là où la brume simulée touche l'eau, puis se résorbe.
            float4 WaterSurface(int k, float3 ray, float sceneDistance, out float tEnter)
            {
                tEnter = 1e9;
                float3 n = _GENormals[k].xyz;
                float den = dot(ray, n);
                if (abs(den) < 1e-3) return 0;
                float t = dot(_GEPoints[k].xyz + n * 0.08 - _GECamera.xyz, n) / den;
                if (t < 0 || t > sceneDistance + 0.3) return 0;
                float3 east, up, north;
                GEBasis(k, east, up, north);
                float3 rel = _GECamera.xyz + ray * t - _GEGridO[k].xyz;
                float2 pl = float2(dot(rel, east), dot(rel, north));      // repère fixe (ne suit pas le jet)
                float2 fromJet = pl - _GESrc[k].xz;
                float r = length(fromJet);
                float ri = max(_GESrc[k].w, 0.3);
                // 1.6 : seulement l'eau brassée sous le jet actuel (≈ 2,5 r_i), plus de grand disque d'écume ni d'écume
                // résiduelle en taches : le reste de l'effet est le nuage d'embruns (volume) et les gouttes.
                float radius = clamp(ri * 2.5, 3.0, 30.0);
                if (r > radius * 1.6) return 0;
                float seed = _GEMisc[k].x;
                float force = _GEJet[k].y * saturate(_GEJet[k].x / 24.0) * _GEJet[k].w;
                if (force < 0.01) return 0;
                float edgeN = N4(float3(pl / max(radius * 1.5, 4.0) + seed, 0.37)).a;
                float mask = force * (1 - smoothstep(0.3, 1.0, r / (radius * (0.75 + 0.5 * edgeN))));
                if (mask < 0.01) return 0;
                // Texture d'écume transportée par l'écoulement de surface.
                float3 v = GEMeanVelocityEx(k, float3(pl.x, _GESrc[k].y + 0.3, pl.y), false);
                float2 vs = v.xz;
                float spd = length(vs);
                vs *= spd > 7.0 ? 7.0 / spd : 1.0;
                const float period = 1.8;
                float tt = _GETime / period;
                float foamTex = 0;
                [unroll] for (int ph = 0; ph < 2; ph++)
                {
                    float phase = frac(tt + ph * 0.5);
                    float cycle = floor(tt + ph * 0.5);
                    float w = 1 - abs(2 * phase - 1);
                    float2 q = pl - vs * (phase * period);
                    // Bouillonnement (échelle ≈ la zone brassée) : eau blanche presque continue au centre, déchirée au bord.
                    float4 a = N4(float3(q / max(radius * 0.9, 3.0), seed + cycle * 0.371));
                    float4 b = N4(float3(q / max(radius * 0.3, 1.5), seed + 0.53 + cycle * 0.217));
                    foamTex += w * (a.g * 0.6 + b.r * 0.4);
                }
                float fadeOut = exp(-_GEColorB[k].w / 1.5);
                float3 light = _GEAmbientSky.rgb + _GEAmbientGround.rgb * 0.25 + _GESunColor.rgb * 0.72;
                float foamA = smoothstep(0.62 - 0.30 * mask, 0.94 - 0.18 * mask, foamTex) * mask * 0.5 * fadeOut;
                float alpha = foamA;
                float3 col = float3(0.9, 0.95, 0.96) * light * foamA;
                tEnter = t;
                return float4(col, alpha);
            }

            float4 fragVolume(v2f_img i) : SV_Target
            {
                if (_GEDebugMode > 1.5) return 0; // diagnostic : coût fixe sans marche
                float3 ray = RayAt(i.uv);
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                #if defined(UNITY_REVERSED_Z)
                bool skyPixel = rawDepth < 0.0001;
                #else
                bool skyPixel = rawDepth > 0.9999;
                #endif
                float3 sceneCol = tex2Dlod(_MainTex, float4(i.uv, 0, 0)).rgb;   // image du jeu sous le pixel
                float sceneDistance = skyPixel ? 1e6 :
                    LinearEyeDepth(rawDepth) / max(dot(ray, normalize(_GECamForward.xyz)), 0.05);
                float2 px = i.uv * _ScreenParams.xy;
                // Hachage 2D décorrélé : la suite linéaire précédente répétait des diagonales visibles
                // sur les grands nuages opaques quand chaque rayon entrait à un pas différent.
                // (1.8 : hachage entier « lowbias32 » de C. Wellons, domaine public, comme dans NoiseBaker ; remplace un
                // extrait sous licence MIT qui aurait exigé une attribution à la publication.)
                uint2 ipx = (uint2)px;
                uint hsh = ipx.x * 0x9E3779B9u ^ (ipx.y + 0x7F4A7C15u) * 0x85EBCA6Bu;
                hsh ^= hsh >> 16; hsh *= 0x7FEB352Du; hsh ^= hsh >> 15; hsh *= 0x846CA68Bu; hsh ^= hsh >> 16;
                float jitter = (hsh & 0xFFFFFFu) / 16777216.0;
                // Normale de la surface visible (dérivées d'écran, hors de toute branche) : le voile du vide ne se pose que
                // sur le sol. Distance bornée pour le ciel (dérivées finies).
                float3 surfW = _GECamera.xyz + ray * min(sceneDistance, 1e5);
                float3 surfN = normalize(cross(ddx(surfW), ddy(surfW)) + 1e-6);
                float4 c[4];
                float te[4];
                // Garder une seule marche compilée : la duplication des quatre foyers dépasse le délai du compilateur DX11.
                [loop] for (int k = 0; k < 4; k++)
                {
                    c[k] = 0; te[k] = 1e9;
                    if (_GEParams[k].x < 0.002) continue;
                    bool waterSlot = _GEParams[k].z > 3.5 && _GEParams[k].z < 4.5 && _GEParams[k].w < 0.5;
                    if ((_GESurfaceGroup == 1 && waterSlot) || (_GESurfaceGroup == 2 && !waterSlot)) continue;
                    if (_GEParams[k].w > 0.5) c[k] = VacuumSheet(k, ray, sceneDistance, skyPixel, _GEParams[k].x, sceneCol, surfN, te[k]);
                    else
                    {
                        if (_GESimBlend > 0.5) c[k] = MarchGrid(k, ray, sceneDistance, jitter, te[k]);
                        // Air raréfié (Duna) : les grains fusent aussi en nappe rasante sous le nuage (1.7).
                        if (_GEExtra[k].y > 0.01)
                        {
                            float ts;
                            float4 sh = VacuumSheet(k, ray, sceneDistance, skyPixel, _GEExtra[k].y, sceneCol, surfN, ts);
                            c[k].rgb += (1 - c[k].a) * sh.rgb;
                            c[k].a += (1 - c[k].a) * sh.a;
                            te[k] = min(te[k], ts);
                        }
                        if (_GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5)
                        {
                            float tw;
                            float4 foam = WaterSurface(k, ray, sceneDistance, tw);
                            c[k].rgb += (1 - c[k].a) * foam.rgb;
                            c[k].a += (1 - c[k].a) * foam.a;
                            te[k] = min(te[k], tw);
                        }
                    }
                }
                // Composition avant → arrière selon la distance d'entrée.
                float4 result = 0;
                [unroll] for (int n = 0; n < 4; n++)
                {
                    int best = 0; float bt = 1e10;
                    [unroll] for (int m = 0; m < 4; m++) if (te[m] < bt) { bt = te[m]; best = m; }
                    if (bt > 1e8) break;
                    float4 v = c[best];
                    result.rgb += (1 - result.a) * v.rgb;
                    result.a += (1 - result.a) * v.a;
                    te[best] = 1e10;
                }
                // Après l'intégration : transparence, ombres et contours restent identiques.
                result.rgb *= _GEEffectBrightness;
                return result;
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment fragCompose
            #include "UnityCG.cginc"
            #include "GEFlow.cginc"
            sampler2D _MainTex, _GELowTex, _CameraDepthTexture;
            sampler3D _GENoise;
            sampler3D _GEField;
            float4 _GEFieldDims;
            float _GESimBlend;
            float4 _GESunDir;
            int _GEShadowSteps;       // ombre des nuages au sol : pas de marche vers le soleil (0 = désactivée)

            // Ombre des nuages simulés sur ce qui est visible à l'écran (sol, bâtiments, lanceur) : courte marche vers le
            // soleil dans chaque grille (1.8). Avec un soleil bas, un gros nuage de lancement projette une longue ombre.
            float CloudShadow(float3 world, float jitter)
            {
                float shadow = 1.0;
                [loop] for (int k = 0; k < 4; k++)
                {
                    if (_GEGridN[k].w < 0.002 || _GESimBlend < 0.5) continue;
                    float3 east, up, north;
                    GEBasis(k, east, up, north);
                    float3 rel = world - _GEGridO[k].xyz;
                    float3 lo = float3(dot(rel, east), dot(rel, up), dot(rel, north));
                    float3 ls = float3(dot(_GESunDir.xyz, east), dot(_GESunDir.xyz, up), dot(_GESunDir.xyz, north));
                    if (ls.y < 0.03) continue;
                    // Entrée et sortie de la grille sur le rayon vers le soleil (test des plans).
                    float E = max(_GEGridO[k].w, 1.0), Hg = max(_GEGridU[k].w, 1.0);
                    float3 inv = 1.0 / (abs(ls) > 1e-5 ? ls : (ls >= 0 ? 1e-5 : -1e-5));
                    float3 ta = (float3(-E, -1.0, -E) - lo) * inv, tb = (float3(E, Hg, E) - lo) * inv;
                    float3 tmin = min(ta, tb), tmax = max(ta, tb);
                    float enter = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                    float leave = min(min(tmax.x, tmax.y), tmax.z);
                    if (leave <= enter) continue;
                    float stepL = (leave - enter) / _GEShadowSteps;
                    float tau = 0;
                    [loop] for (int s = 0; s < 14; s++)
                    {
                        if (s >= _GEShadowSteps) break;
                        float3 uvw = GEGridUv(k, lo + ls * (enter + (s + jitter) * stepL));
                        if (all(uvw > 0.0) && all(uvw < 1.0))
                        {
                            float zc = clamp(uvw.z, 0.5 / _GEFieldDims.z, 1.0 - 0.5 / _GEFieldDims.z);
                            float f = tex3Dlod(_GEField, float4(uvw.x, uvw.y, (k + zc) / _GEFieldDims.w, 0)).r;
                            // Mêmes fondus que le rendu (bords et plafond de la grille), et une brume presque invisible
                            // ne fait pas d'ombre (sinon grande tache sombre au bord net sous un nuage qui se dissipe).
                            float2 ed = min(uvw.xz, 1.0 - uvw.xz);
                            f *= smoothstep(0.0, 0.07, min(ed.x, ed.y)) * smoothstep(0.0, 0.1, 1.0 - uvw.y);
                            tau += f * smoothstep(0.3, 0.9, f);
                        }
                    }
                    shadow *= exp(-tau * stepL * 0.1 * _GEGridN[k].w);
                }
                return shadow;
            }
            float4 _GELowTexel;
            float _GEDetailLevel;
            float _GENight;
            float _GEVolFar;          // au-delà, aucun nuage : ciel et sol lointain sont équivalents pour le suréchantillonnage
            float4 _GECamera, _GECamForward, _GERay00, _GERay10, _GERay01, _GERay11;
            float4 _GEMarkPoints[16], _GEMarkNormals[16], _GEMarkAges[16];
            float4 _GESunColor;
            int _GEMarkCount, _GESurfaceGroup;

            float3 RayAt(float2 uv)
            {
                float3 a = lerp(_GERay00.xyz, _GERay10.xyz, uv.x);
                float3 b = lerp(_GERay01.xyz, _GERay11.xyz, uv.x);
                return normalize(lerp(a, b, uv.y));
            }
            float EyeDepth(float2 uv)
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if defined(UNITY_REVERSED_Z)
                return raw < 0.0001 ? 1e6 : LinearEyeDepth(raw);
                #else
                return raw > 0.9999 ? 1e6 : LinearEyeDepth(raw);
                #endif
            }
            float4 N4(float3 p) { return tex3Dlod(_GENoise, float4(p, 0)); }

            fixed4 fragCompose(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float3 baseColor = tex2D(_MainTex, uv).rgb;
                float3 ray = RayAt(uv);
                float eye = EyeDepth(uv);
                if (_GESurfaceGroup != 2 && _GEMarkCount > 0 && eye < 1e5)
                {
                    // Décalques en espace écran : position reconstruite depuis la profondeur, masque par la normale
                    // reconstruite (on ne peint pas les pieds du lanceur).
                    float3 world = _GECamera.xyz + ray * (eye / max(dot(ray, normalize(_GECamForward.xyz)), 0.05));
                    float3 dx = ddx(world), dy = ddy(world);
                    float3 surfaceN = normalize(cross(dx, dy));
                    [loop] for (int m = 0; m < 16; m++)
                    {
                        if (m >= _GEMarkCount) break;
                        float3 d = world - _GEMarkPoints[m].xyz;
                        float3 n = normalize(_GEMarkNormals[m].xyz);
                        float height = abs(dot(d, n));
                        float radius = _GEMarkPoints[m].w;
                        float3 planar = d - n * dot(d, n);
                        float r = length(planar);
                        // 1.8 : tolérance de hauteur élargie (relief de Parallax : le sol affiché s'écarte de quelques mètres
                        // du plan de l'impact ; la trace le suit au lieu d'être trouée sur chaque bosse).
                        float hTol = max(4.0, radius * 0.15);
                        if (r > radius * 2.2 || height > hTol) continue;
                        float align = smoothstep(0.45, 0.8, abs(dot(surfaceN, n)));
                        float contact = align * (1 - smoothstep(0.3 * hTol, hTol, height));
                        float3 bx = abs(n.y) < 0.9 ? normalize(cross(n, float3(0, 1, 0))) : normalize(cross(n, float3(0, 0, 1)));
                        float3 bz = cross(n, bx);
                        float2 pm = float2(dot(planar, bx), dot(planar, bz));   // mètres
                        float2 p = pm / max(radius, 0.1);
                        float rr = length(p);
                        float seed = _GEMarkAges[m].z * 0.013;
                        float strength = saturate(abs(_GEMarkNormals[m].w));
                        float grow = 0.35 + 0.65 * pow(strength, 0.7);          // la trace s'étend avec la dose
                        float x = rr / grow;
                        // Références (pas d'atterrissage de Falcon 9, lander Morpheus) : tache sombre au contour irrégulier
                        // mais doux, marbrée ; quelques traînées larges vers le bord ; jamais un disque net ni des aiguilles.
                        float soot01 = _GEMarkAges[m].w;            // kérolox ≈ 1 (suie), méthalox ≈ 0,2, hydrolox ≈ 0
                        // Contour irrégulier en mètres (bruit ancré à la trace, deux échelles douces) : jamais un disque,
                        // jamais d'aiguilles ni de mouchetures au pixel.
                        float fb = N4(float3(pm / max(radius * 2.2, 1.0) + seed, 0.13)).a * 0.8
                                 + N4(float3(pm / max(radius * 1.2, 0.5) + seed, 0.61)).a * 0.2;
                        float xr = x / (0.8 + 0.45 * (fb - 0.5));
                        // Trois échelles ancrées au sol : contour déchiré, plaques brûlées et grain fin.
                        float grain = N4(float3(pm * 0.09 + seed, 0.77)).r * 0.45
                                    + N4(float3(pm * 0.3 + seed, 0.29)).a * 0.35
                                    + N4(float3(pm * 0.85 + seed, 0.51)).b * 0.20;
                        if (_GEMarkNormals[m].w < 0)
                        {
                            // Vide : halo clair de décapage (comme autour des sites Apollo), discret, sans bord marqué.
                            // 1.7 : zone de souffle nettement visible (images LRO des sites Apollo : auréole claire sur
                            // ~100 m, bord irrégulier ; au pied du module, sol griffé en stries radiales, plus sombre là
                            // où la poussière fine a été balayée).
                            // (Traces de la 1.8, rétablies en 1.9.2 : auréole striée et sol griffé, plus une tache uniforme.)
                            float2 dirv = p / max(rr, 1e-3);
                            float rays = smoothstep(0.45, 0.75, N4(float3(dirv * 0.3 + seed, seed * 0.3)).a);
                            float s07 = pow(strength, 0.6);
                            float halo = contact * s07 * (1 - smoothstep(0.35, 1.05, xr)) * (0.65 + 0.35 * rays) * (0.75 + 0.25 * grain);
                            baseColor = lerp(baseColor, baseColor * 1.35 + 0.02, saturate(halo * 0.85));
                            float streaksV = smoothstep(0.4, 0.75, N4(float3(dirv * 6.0 + seed, rr * 0.8 + seed)).a);
                            float scourV = contact * s07 * (1 - smoothstep(0.08, 0.32, xr));
                            baseColor *= lerp(1.0, lerp(0.72, 0.9, streaksV), scourV);
                        }
                        else
                        {
                            // 1.8 : trace qui dépend du sol réellement affiché sous le pixel (herbe, terre, sable, béton ; avec
                            // ou sans Parallax). Références : essais de moteurs sur herbe (herbe roussie autour, carbonisée au
                            // centre), zones d'atterrissage au kérosène (béton noirci), pas de tir au méthalox (béton à peine
                            // grisé : il brûle propre), sites martiens (zone de souffle plus sombre, la poussière claire est
                            // balayée). Plus d'étoile de traînées ni de disque délavé.
                            bool pad = _GEMarkAges[m].y > 1.5 && _GEMarkAges[m].y < 2.5;
                            float lum = dot(baseColor, float3(0.3, 0.55, 0.15));
                            // Végétation : le vert domine la couleur du sol affiché.
                            float veg = pad ? 0.0 : saturate((baseColor.g - max(baseColor.r, baseColor.b)) / max(lum, 0.04) * 4.0);
                            float s07 = contact * pow(strength, 0.7);
                            float zone = (1 - smoothstep(0.35, 1.15, xr)) * (0.55 + 0.80 * grain);
                            float core = 1 - smoothstep(0.0, 0.55, xr);                           // sous le jet
                            // Herbe : garder sa texture visible ; roussir les bords, carboniser des plaques au centre.
                            float3 straw = lum * float3(0.95, 0.78, 0.50);
                            float3 charred = lum * float3(0.24, 0.21, 0.18);
                            float3 grass = lerp(baseColor, straw, saturate(zone * s07 * 0.42));
                            grass = lerp(grass, charred, saturate(core * s07 * (0.55 + 0.70 * grain)));
                            // Sol nu et béton : le grain de la surface reste lisible sous la zone noircie.
                            float3 burnt = lerp(baseColor * lerp(1.0, lerp(0.56, 0.82, grain), saturate(zone * s07)), grass, veg);
                            // Suie selon l'ergol (kérolox gris-noir, méthalox gris léger, hydrolox presque rien), surtout au
                            // cœur, en voiles marbrés.
                            float sootAmt = lerp(0.26, 0.92, soot01);
                            float sootMask = saturate((0.85 * core + 0.35 * zone) * s07 * (0.45 + 0.95 * grain)) * sootAmt;
                            baseColor = burnt * lerp(float3(1, 1, 1), float3(0.2, 0.19, 0.18), sootMask);
                            // Sol chauffé par le jet : cœur jaune-orangé sous le jet, qui refroidit vers le rouge sombre en une
                            // trentaine de secondes ; visible de jour, forte la nuit. Doux et marbré, jamais moucheté.
                            float temp = exp(-_GEMarkAges[m].x / 12.0) * saturate(strength * 3.0);
                            float3 hotCol = lerp(float3(0.5, 0.05, 0.01), float3(1.0, 0.45, 0.1), saturate(temp * 1.2));
                            float hot = (1 - smoothstep(0.0, 0.42, xr)) * (0.55 + 0.45 * grain);
                            baseColor += hotCol * pow(temp, 1.4) * contact * hot * (0.85 + 1.3 * _GENight);
                        }
                    }
                }
                // Ombre des nuages : le soleil direct est masqué, la lumière du ciel reste (≈ 35 %).
                if (_GEShadowSteps > 0 && _GESurfaceGroup != 2 && eye < 1e5 && _GENight < 0.95)
                {
                    float3 worldS = _GECamera.xyz + ray * (eye / max(dot(ray, normalize(_GECamForward.xyz)), 0.05));
                    // Échantillons au milieu des pas (un décalage aléatoire, par pixel ou par bruit doux, faisait du grain ou des
                    // taches « camouflage » ; les bandes venaient de la brume résiduelle, écartée par le seuil de densité).
                    baseColor *= lerp(1.0, CloudShadow(worldS, 0.5), 0.65 * (1.0 - _GENight));
                }
                // Suréchantillonnage bilatéral guidé par la profondeur (pas de halo sur les bords).
                float eyeC = min(eye, max(_GEVolFar, 1.0));
                float2 texel = _GELowTexel.xy;
                float2 pixel = uv / texel - 0.5;
                float2 baseUv = (floor(pixel) + 0.5) * texel;
                float4 sum = 0;
                float weightSum = 0;
                int lo = _GEDetailLevel > 1.5 ? -1 : 0, hi = _GEDetailLevel > 1.5 ? 3 : 2;
                [loop] for (int y = lo; y < hi; y++)
                [loop] for (int x = lo; x < hi; x++)
                {
                    float2 tap = baseUv + float2(x, y) * texel;
                    float4 v = tex2D(_GELowTex, tap);
                    float nearby = min(EyeDepth(tap), max(_GEVolFar, 1.0));
                    float dw = exp(-abs(eyeC - nearby) / max(1.5, eyeC * 0.01));
                    float2 off = pixel - (floor(pixel) + float2(x, y));
                    float sw = exp(-dot(off, off) * 0.9);
                    float w = dw * sw + 1e-4;
                    sum += v * w;
                    weightSum += w;
                }
                float4 volume = sum / max(weightSum, 1e-4);
                return float4(baseColor * (1 - volume.a) + volume.rgb, 1);
            }
            ENDCG
        }
        // Passe 2 (1.8) : couleur réelle du sol autour de chaque foyer, lue dans l'image du jeu (avant nos effets) en 16
        // points au sol autour de l'impact, gardés s'ils sont visibles (profondeur cohérente, ni pièce ni bâtiment devant).
        // Moyenne glissante (≈ 1,5 s) par foyer dans une texture 4 × 1 : la poussière prend la teinte du sol affiché
        // (textures du jeu, Parallax ou autres) ; l'herbe donne de la terre (pas de poussière verte). La nuit, la dernière
        // couleur de jour est gardée.
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment fragGroundColor
            #include "UnityCG.cginc"
            #include "GEFlow.cginc"
            sampler2D _CameraDepthTexture;
            sampler2D _MainTex;
            sampler2D _GEGroundColPrev;
            float4 _GEGroundColW[4];   // x = poids de la couleur du sol, y = remise à zéro, z = pas de temps (s)
            float4 _GEParams[4];
            float4 _GECamera, _GECamForward, _GERay00, _GERay10, _GERay01, _GERay11;
            float4 _GESunDir, _GESunColor, _GEAmbientSky;

            // Inverse de RayAt : coordonnées d'écran d'une direction (repère symétrique : les rayons des coins sont sur un plan).
            bool UvOfDir(float3 d, out float2 uv)
            {
                uv = 0;
                float3 f = normalize(_GECamForward.xyz);
                float df = dot(d, f);
                if (df < 0.05) return false;
                float3 p = d / df;
                float3 c00 = _GERay00.xyz / dot(_GERay00.xyz, f);
                float3 ex = _GERay10.xyz / dot(_GERay10.xyz, f) - c00, ey = _GERay01.xyz / dot(_GERay01.xyz, f) - c00;
                uv = float2(dot(p - c00, ex) / dot(ex, ex), dot(p - c00, ey) / dot(ey, ey));
                return all(uv > 0.01) && all(uv < 0.99);
            }

            float4 fragGroundColor(v2f_img i) : SV_Target
            {
                int k = min((int)(i.uv.x * 4.0), 3);
                float4 prev = tex2Dlod(_GEGroundColPrev, float4((k + 0.5) / 4.0, 0.5, 0, 0));
                if (_GEGroundColW[k].y > 0.5) prev = 0;
                if (_GEParams[k].x < 0.002 || _GEGroundColW[k].x < 0.01) return prev;
                float3 east, up, north;
                GEBasis(k, east, up, north);
                const float3 lw = float3(0.3, 0.55, 0.15);
                // Lumière du jour sur le sol : sans elle (nuit), la couleur lue n'est que celle des lampes.
                float light = dot(_GESunColor.rgb, lw) * saturate(dot(_GESunDir.xyz, up)) + dot(_GEAmbientSky.rgb, lw) * 0.8;
                if (light < 0.25) return prev;
                float ri = max(_GESrc[k].w, 0.5);
                float3 fwd = normalize(_GECamForward.xyz);
                float3 sum = 0;
                float wsum = 0;
                [loop] for (int j = 0; j < 16; j++)
                {
                    // Deux anneaux autour de l'impact (hors de la tache chauffée et éclairée par la flamme).
                    float ang = (j + 0.5) * 0.3926991 + (j >= 8 ? 0.19635 : 0.0);
                    float rad = (j < 8 ? 2.0 : 4.0) * max(ri, 3.0);
                    float2 xz = _GESrc[k].xz + rad * float2(cos(ang), sin(ang));
                    float y = GEGroundHeight(k, xz) + 0.2;
                    float3 world = _GEGridO[k].xyz + east * xz.x + up * y + north * xz.y;
                    float3 dir = world - _GECamera.xyz;
                    float dist = length(dir);
                    dir /= max(dist, 1e-3);
                    float2 uv;
                    if (!UvOfDir(dir, uv)) continue;
                    float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0));
                    #if defined(UNITY_REVERSED_Z)
                    if (raw < 0.0001) continue;
                    #else
                    if (raw > 0.9999) continue;
                    #endif
                    float sceneDist = LinearEyeDepth(raw) / max(dot(dir, fwd), 0.05);
                    if (abs(sceneDist - dist) > max(2.5, 0.05 * dist)) continue;
                    // Au loin, la brume de l'atmosphère teinte le sol : poids réduit.
                    float w = 1.0 - smoothstep(400.0, 2000.0, dist);
                    sum += tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb * w;
                    wsum += w;
                }
                if (wsum < 0.5) return prev;
                // Albédo : couleur lue divisée par la lumière reçue (soleil + ciel), en ne retirant que la moitié de sa teinte :
                // le jaune du soleil bas ne passe pas dans la poussière, sans que la neige bleutée ne vire au beige.
                float3 illum = _GESunColor.rgb * saturate(dot(_GESunDir.xyz, up)) + _GEAmbientSky.rgb * 0.8;
                illum = lerp(dot(illum, lw).xxx, illum, 0.5);
                float3 c = sum / wsum / max(illum, 0.08);
                float lum = max(dot(c, lw), 1e-3);
                // Teinte un peu désaturée (la poussière est plus terne que le sol).
                float3 chroma = clamp(lerp(1.0, c / lum, 0.9), 0.0, 2.5);
                // Herbe (le vert domine) : c'est la terre dessous qui vole, brune et plus sombre.
                float veg = saturate((c.g - max(c.r, c.b)) / lum * 4.0);
                chroma = lerp(chroma, float3(1.2, 0.97, 0.72), veg);
                float albLum = clamp(lum, 0.12, 0.95) * lerp(1.0, 0.8, veg);
                float3 alb = saturate(chroma * albLum);
                float rate = saturate(_GEGroundColW[k].z / 1.5) * saturate(wsum / 6.0);
                if (prev.a < 0.01) rate = saturate(wsum / 6.0);
                return float4(lerp(prev.rgb, alb, rate), lerp(prev.a, 1.0, rate));
            }
            ENDCG
        }
        // Passe 3 (1.0.2) : profondeur du nuage opaque dans le tampon de profondeur. Scatterer (versions publiques) dessine son
        // ciel APRÈS nos nuages, sur tout pixel resté à la profondeur du ciel : vu devant le ciel, le nuage était remplacé par du
        // ciel (et les nuages EVE lointains passaient devant lui). On écrit la distance de la partie dense du nuage là où il est
        // opaque (calculée par la passe 4) : ce qui est derrière lui est caché, ce qui est devant (panache de la fusée) reste
        // visible. La couleur n'est pas modifiée.
        Pass
        {
            // Couleur inchangée par le mélange (Zero, One) : avec « ColorMask 0 », la profondeur n'était pas écrite (banc).
            Blend Zero One
            ZWrite On
            ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment fragDepth
            #include "UnityCG.cginc"
            sampler2D_float _GECloudDepth;   // passe 4 : r = profondeur brute, g = 1 là où le nuage est devant la scène

            fixed4 fragDepth(v2f_img i, out float outDepth : SV_Depth) : SV_Target
            {
                float2 m = tex2D(_GECloudDepth, i.uv).rg;
                outDepth = m.r;
                if (m.g < 0.5) discard;
                return 0;
            }
            ENDCG
        }
        // Passe 4 (1.0.2) : carte de profondeur de la scène avec la partie opaque du nuage, en pleine résolution. Elle sert à la
        // passe 3 et remplace _CameraDepthTexture pour tout ce qui est dessiné après nous : la fumée d'autres mods qui se cale
        // sur cette carte (traînées volumétriques des boosters, par exemple) ne passe plus devant notre nuage quand elle est
        // derrière lui ou dedans (ticket : « deuxième panache dans le premier », montagnes visibles à travers la fumée).
        Pass
        {
            ZWrite Off
            ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment fragCloudDepth
            #include "UnityCG.cginc"
            #include "GEFlow.cginc"
            sampler2D _GELowTex, _CameraDepthTexture;
            sampler3D _GEField;
            float4 _GEFieldDims;
            float4 _GEParams[4];
            float _GESimBlend;
            float4 _GECamera, _GECamForward, _GERay00, _GERay10, _GERay01, _GERay11;

            float3 RayAt(float2 uv)
            {
                float3 a = lerp(_GERay00.xyz, _GERay10.xyz, uv.x);
                float3 b = lerp(_GERay01.xyz, _GERay11.xyz, uv.x);
                return normalize(lerp(a, b, uv.y));
            }

            float FieldAt(int k, float3 p)
            {
                float3 uvw = GEGridUv(k, p);
                if (any(uvw <= 0.0) || any(uvw >= 1.0)) return 0;
                float zc = clamp(uvw.z, 0.5 / _GEFieldDims.z, 1.0 - 0.5 / _GEFieldDims.z);
                return tex3Dlod(_GEField, float4(uvw.x, uvw.y, (k + zc) / _GEFieldDims.w, 0)).r;
            }

            float4 fragCloudDepth(v2f_img i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                if (_GESimBlend < 0.5 || tex2D(_GELowTex, i.uv).a < 0.5) return float4(raw, 0, 0, 0);
                float3 ray = RayAt(i.uv);
                float fwd = max(dot(ray, normalize(_GECamForward.xyz)), 0.05);
                #if defined(UNITY_REVERSED_Z)
                bool sky = raw < 0.0001;
                #else
                bool sky = raw > 0.9999;
                #endif
                float scene = sky ? 1e9 : LinearEyeDepth(raw) / fwd;
                float best = 1e9;
                [loop] for (int k = 0; k < 4; k++)
                {
                    // Nuages sur grille seulement (pas la nappe du vide), visibles.
                    if (_GEParams[k].x < 0.002 || _GEParams[k].w > 0.5 || _GEGridN[k].w < 0.002) continue;
                    float3 east, up, north;
                    GEBasis(k, east, up, north);
                    float E = max(_GEGridO[k].w, 1.0), Hg = max(_GEGridU[k].w, 1.0);
                    float3 o = _GECamera.xyz - _GEGridO[k].xyz;
                    float3 lo = float3(dot(o, east), dot(o, up), dot(o, north));
                    float3 ld = float3(dot(ray, east), dot(ray, up), dot(ray, north));
                    float3 inv = 1.0 / (abs(ld) > 1e-6 ? ld : (ld >= 0 ? 1e-6 : -1e-6));
                    float3 ta = (float3(-E, -1.0, -E) - lo) * inv, tb = (float3(E, Hg, E) - lo) * inv;
                    float3 tmin = min(ta, tb), tmax = max(ta, tb);
                    float enter = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                    float leave = min(min(min(tmax.x, tmax.y), tmax.z), min(scene, best));
                    if (leave <= enter) continue;
                    float cell = 2.0 * E / max(_GEFieldDims.x, 1.0);
                    float stepL = max(cell * 0.75, (leave - enter) / 160.0);
                    // Surface = là où le nuage rendu devient à moitié opaque (même extinction et même couverture que la
                    // passe 0, sans le détail fin) : un seuil fixe sur la densité brute laissait passer les fumées d'autres
                    // mods sur le haut des bouffées, plus diluées mais visiblement opaques.
                    bool waterK = _GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5;
                    float Rm = max(_GEFlowP[k].z, 4.0);
                    float sigma = (waterK ? 0.16 : lerp(0.2, 0.28, _GEJet[k].z)) * saturate(90.0 / Rm + 0.75) * _GEParams[k].x;
                    float covTop = waterK ? 1.2 : lerp(1.6, 0.6, _GEJet[k].z);
                    float tau = 0;
                    [loop] for (float t = enter; t < leave; t += stepL)
                    {
                        float f = FieldAt(k, lo + ld * t);
                        tau += smoothstep(waterK ? 0.03 : 0.0, covTop, f) * sigma * stepL;
                        if (tau > 0.7) { best = min(best, t); break; }
                    }
                }
                if (best >= scene || best > 1e8) return float4(raw, 0, 0, 0);
                float eye = best * fwd;
                return float4((1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z, 1, 0, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
