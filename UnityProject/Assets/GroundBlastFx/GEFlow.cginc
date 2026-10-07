// GroundBlastFx — écoulement partagé entre la simulation (VolumeField.compute) et le rendu (GroundVolume.shader).
// Repère local d'un foyer : origine = point d'ancrage FIXE du nuage au sol (ne suit pas le jet),
// x = est, y = verticale locale, z = nord. Unités : mètres et secondes.
#ifndef GE_FLOW_INCLUDED
#define GE_FLOW_INCLUDED

float4 _GEGridO[4];    // ancrage monde.xyz, E = demi-étendue horizontale de la grille (m)
float4 _GEGridU[4];    // verticale monde.xyz, Hg = hauteur de la grille (m)
float4 _GEGridN[4];    // nord monde.xyz, visibilité 0..1
float4 _GESrc[4];      // point d'impact courant (repère local).xyz, r_i
float4 _GEJet[4];      // vitesse du jet pariétal u_i (m/s, 0 si coupé), apport 0..1, part de vapeur, moteurs actifs
float4 _GEFlowP[4];    // vent local x, vent local z (m/s), R_max, taux de disparition (1/s)
float4 _GEJetDir[4];   // composante tangentielle du jet (x, z ; |.| = sin α), direction de tranchée (x, z ; 0 = radiale)
float4 _GEMisc[4];     // graine, type de surface, âge (s), hauteur de la tuyère au-dessus de l'impact (m)
float4 _GESrcP[4];     // rayon de l'anneau source, largeur, hauteur (m, ≥ 1 maille), débit (densité/s pour un apport de 1)
float4 _GEOutlets[8];  // bouches du déflecteur de flammes (deux par foyer, repère local) : x, z, hauteur de la bouche, poids (0 = absente)
float4 _GEOutletGround[4]; // sol devant les bouches 0 et 1 : dénivelé jusqu'au terrain (m), distance où il est à moitié franchi (m)
float4 _GEJetX[4];     // jets visibles des bouches : longueur L (m), intensité 0..1, rayon à la bouche r0 (m), décrochage (m)
float4 _GEJetMotion[4]; // vitesse mémorisée (m/s), raccord aux bouffées de pad (0/1), réservés
float4 _GEOutletDir[4]; // sens de sortie des bouches 0 et 1 (x, z ; 0 = depuis l'ancrage)

#define GE_JET_OPENING 0.2   // la vapeur s'élargit dès la bouche avant de rejoindre le nuage de surface

// Carte des hauteurs du sol (1.7) : 48 × 48 par foyer, bandes empilées en v ; hauteur locale (origine de la grille).
Texture2D _GEGround;
SamplerState sampler_GEGround;
float4 _GEGroundInfo[4];   // x = carte valide, y = niveau de l'ancrage (m, local)

// Hauteur du sol (repère local) sous le point xz du foyer k. Sans carte : sol plat au niveau de l'ancrage.
float GEGroundHeight(int k, float2 xz)
{
    if (_GEGroundInfo[k].x < 0.5) return _GEGroundInfo[k].y;
    float E = max(_GEGridO[k].w, 1.0);
    float2 uv = saturate(xz / (2.0 * E) + 0.5);
    const float R = 48.0;
    uv.y = (k + clamp(uv.y, 0.5 / R, 1.0 - 0.5 / R)) / 4.0;
    uv.x = clamp(uv.x, 0.5 / R, 1.0 - 0.5 / R);
    return _GEGround.SampleLevel(sampler_GEGround, uv, 0).r;
}

// Sens de sortie horizontal de la bouche i : émission de la fumée stock (réelle), sinon depuis l'ancrage du nuage.
float2 GEOutletDirection(int k, int i, float4 o)
{
    float2 d = i == 0 ? _GEOutletDir[k].xy : _GEOutletDir[k].zw;
    return dot(d, d) > 0.25 ? normalize(d) : normalize(o.xy + float2(1e-4, 0));
}
float4 _GEFlowX[4];    // part du jet canalisée par le déflecteur 0..1, vitesse en sortie de bouche (m/s), demi-largeur des bouches (m), R_front (m)

// Épaisseur de la couche pariétale (m) : croît avec la distance (≈ 0,1 r, comme un jet de paroi réel) et vaut au moins
// une maille verticale de la grille (sinon le souffle rasant ne serait pas résolu et la matière monterait sur place).
float GEWallLayer(int k, float r)
{
    return max(0.45 * max(_GESrc[k].w, 0.5) + 0.08 * r, 1.3 * _GESrcP[k].z);
}

// Tourbillon de Lamb-Oseen dans un plan vertical (d = position relative au cœur : horizontale, verticale).
// Sens du rouleau d'un front de jet pariétal : dehors au ras du sol, vers le haut au front, retour par-dessus.
// Portée limitée à quelques rayons de cœur (pas d'effet à distance).
float2 GERoll(float2 d, float a, float vmax)
{
    float rho2 = dot(d, d);
    float aa = a * a;
    float f = vmax * a * 1.567 * (1.0 - exp(-rho2 / aa)) / max(rho2, 1e-3) * exp(-rho2 / (9.0 * aa));
    return float2(-d.y, d.x) * f;
}

// Forme d'un jet de bouche (sans bruit, 0..1) : cône rasant qui part de la bouche et suit le sol devant elle,
// comme les panaches du pas de tir de KSP 2 et des mods de panaches volumétriques. s = distance le long du jet,
// rr = rayon local du cône, lat/dy = écarts au centre (m). N'inclut pas l'intensité du jet.
float GEJetShape(int k, int i, float3 p, out float s, out float rr, out float lat, out float dy)
{
    s = -1; rr = 1; lat = 0; dy = 0;
    float4 o = _GEOutlets[2 * k + i];
    float4 jx = _GEJetX[k];
    if (o.w < 0.01 || jx.x < 0.5) return 0;
    float2 g = i == 0 ? _GEOutletGround[k].xy : _GEOutletGround[k].zw;
    float2 d = GEOutletDirection(k, i, o);
    float2 rel = p.xz - o.xy;
    s = dot(rel, d);
    float L = jx.x;
    if (s < jx.w - 4.0 || s > 1.2 * L) return 0;
    lat = rel.x * d.y - rel.y * d.x;
    float sp = max(s, 0.0);
    rr = jx.z + GE_JET_OPENING * sp;
    // Bouche surélevée : la flamme part dans l'ouverture sous la lèvre, pas en boule au-dessus.
    // Correction de rendu seulement ; le nuage et la forme en aval restent ceux du tir validé.
    float mouth = saturate(_GEJetMotion[k].y) * saturate(g.x)
                * (1.0 - smoothstep(2.0, max(2.0 * g.y + 4.0, 8.0), sp));
    rr *= 1.0 - 0.3 * mouth;
    float x01 = saturate((s - g.y + 6.0) / 12.0);
    float groundY = o.z - g.x * x01 * x01 * (3.0 - 2.0 * x01);
    // Axe : à la hauteur de la bouche à la sortie, puis collé au sol (le jet rampe et s'épaissit vers le haut).
    float yc = lerp(o.z + 0.3 * rr, groundY + 0.55 * rr, saturate(sp / (g.y + 10.0)));
    float halfHeight = lerp(rr, min(rr, max(0.45 * g.x, 1.0)), mouth);
    yc = lerp(yc, o.z - 0.5 * g.x, mouth);
    groundY = lerp(groundY, o.z - g.x, mouth);
    dy = (p.y - yc) * (p.y > yc ? 1.0 : 1.2) * (rr / halfHeight);
    float rho2 = (lat * lat + dy * dy) / (rr * rr);
    float shape = exp(-1.4 * rho2);
    // Naît dans la bouche ; après la coupure, le jet se décroche de la bouche (jx.w avance) et part avec le nuage.
    shape *= smoothstep(jx.w - 3.0, jx.w, s) * (1.0 - smoothstep(0.75 * L, 1.2 * L, s));
    return p.y > groundY - 0.3 ? shape : 0.0;
}

// Jet de sortie d'une bouche du déflecteur (vidéos de décollages : Saturn V, SLS, Falcon 9 au LC-39A) :
// un jet turbulent rasant part de la bouche vers l'extérieur, s'élargit (≈ 0,2 m par m), ralentit en 1/x,
// et sa tête s'enroule et monte. o = (x, z, hauteur de la bouche, poids). Renvoie la vitesse ; speed = vitesse locale.
// g = (dénivelé, distance) du sol devant la bouche : sur un pas surélevé, le jet rasant suit la pente jusqu'au terrain
// (effet Coandă) au lieu de continuer à l'horizontale au niveau de la table (nuage qui « flottait »).
float3 GEOutletJet(int k, int i, float3 p, float4 o, float2 g, float Rm, out float speed)
{
    speed = 0;
    if (o.w < 0.01) return 0;
    float4 fx = _GEFlowX[k];
    float Uo = fx.y, bo = max(fx.z, 1.0);
    float2 d = GEOutletDirection(k, i, o);
    float2 rel = p.xz - o.xy;
    float s = dot(rel, d);
    float latS = rel.x * d.y - rel.y * d.x;
    float lat = abs(latS);
    float sp = max(s, 0.0);
    float b = bo + 0.20 * sp;                       // demi-largeur
    float h = 0.9 * bo + 0.12 * sp;                 // vapeur volumineuse mais toujours rasante
    float reachO = 0.7 * Rm + 2.0 * bo;
    float uc = Uo * (5.0 * bo) / (5.0 * bo + sp) * exp(-(sp * sp) / (reachO * reachO));
    // Au-delà du bout du jet visible, le souffle s'essouffle : la vapeur s'y accumule et bourgeonne (pas de fuite au loin).
    float Lj = _GEJetX[k].x;
    if (Lj > 1.0) uc *= 1.0 - 0.85 * smoothstep(0.4 * Lj, 1.0 * Lj, sp);
    float x01 = saturate((s - g.y + 6.0) / 12.0);
    float groundY = o.z - g.x * x01 * x01 * (3.0 - 2.0 * x01);
    float slope = -g.x * x01 * (1.0 - x01) * 0.5;         // dSol/ds (dérivée du lissage sur 12 m)
    float yl = p.y - groundY;
    float vert = yl >= 0.0 ? exp(-(yl * yl) / (h * h)) : exp(-(yl * yl) / (0.25 * h * h));
    float behind = smoothstep(-2.5 * bo, 0.0, s);   // dans la tranchée : l'écoulement converge vers la bouche
    float u = uc * exp(-(lat * lat) / (b * b)) * vert * behind * o.w;
    speed = u;
    float3 v;
    // Étalement latéral et entraînement de l'air ambiant vers le jet (bords qui s'enroulent).
    float spread = 0.12 * u * clamp(latS / b, -1.5, 1.5);
    float2 side = float2(d.y, -d.x);
    v.xz = d * u + side * spread;
    // Jet rasant : il suit le sol (pente), il ne monte pas (1.6 : +6 % de sa vitesse soulevait tout le nuage).
    v.y = u * slope;
    // Tête du jet : suit le front du nuage, s'enroule et monte (gros bourgeonnements qui se retournent).
    float sHead = clamp(fx.w - length(o.xy), 3.0 * bo, reachO);
    float a = 0.9 * b + 2.0;
    float uHead = Uo * (5.0 * bo) / (5.0 * bo + sHead) * exp(-(sHead * sHead) / (reachO * reachO));
    float bHead = bo + 0.20 * sHead;
    float2 roll = GERoll(float2(s - (sHead - a), max(yl, 0.0) - 1.1 * a), a, 0.2 * uHead)
                  * exp(-(lat * lat) / (1.5 * bHead * bHead)) * o.w;
    v.xz += d * roll.x;
    v.y += roll.y;
    return v;
}

// Bout des jets du déflecteur (1.8, 0..1) : là où le jet rasant s'essouffle, la vapeur chaude monte en gros
// bourgeons (vidéos de décollages : le nuage de la tranchée grandit en hauteur vers son bout, ce n'est pas un tube
// d'épaisseur constante). Pondéré par l'intensité du jet : quand il s'éteint, les tours cessent de monter.
float GEPadTip(int k, float3 p)
{
    float Lj = _GEJetX[k].x;
    if (Lj < 1.0 || _GEJetX[k].y < 0.003) return 0;
    float bo = max(_GEFlowX[k].z, 1.0);
    float tip = 0;
    [unroll] for (int i = 0; i < 2; i++)
    {
        float4 o = _GEOutlets[2 * k + i];
        float2 d = GEOutletDirection(k, i, o);
        float2 rel = p.xz - o.xy;
        float s = dot(rel, d);
        float lat = rel.x * d.y - rel.y * d.x;
        float b = bo + 0.3 * max(s, 0.0);
        float along = smoothstep(0.25 * Lj, 0.7 * Lj, s) * (1.0 - smoothstep(1.3 * Lj, 1.9 * Lj, s));
        tip = max(tip, (o.w > 0.01 ? 1.0 : 0.0) * along * exp(-(lat * lat) / (b * b)));
    }
    return tip * saturate(_GEJetX[k].y * 1.5);
}

// Vitesse moyenne de l'écoulement (sans turbulence).
// Jet pariétal radial depuis l'impact COURANT, qui ralentit et s'enroule au front (tourbillon torique), jets de sortie
// du déflecteur sur un pas de tir, flottabilité de la vapeur, sédimentation de la poussière, vent.
// full = false (rendu) : sans rouleau ni jets de bouches, qui ne servent qu'à la simulation ; le rendu n'utilise la
// vitesse que pour faire glisser le détail fin, plafonnée à quelques m/s (coût par échantillon réduit).
float3 GEMeanVelocityEx(int k, float3 p, bool full)
{
    float4 src = _GESrc[k], jet = _GEJet[k], fl = _GEFlowP[k], jd = _GEJetDir[k], fx = _GEFlowX[k];
    float ri = max(src.w, 0.5);
    float Rm = max(fl.z, 4.0 * ri);
    float U = jet.x;
    float steam = jet.z;
    float channel = fx.x;
    float2 rel = p.xz - src.xz;
    float r = length(rel);
    float2 dir = r > 1e-3 ? rel / r : float2(1, 0);
    // Jet incliné : plus de débit du côté où pointe la composante tangentielle.
    // (Modéré en 1.7 : le nuage ne doit pas « prendre l'inclinaison » de la fusée.)
    float asym = saturate(1.0 + 0.6 * dot(dir, jd.xy));
    bool water = _GEMisc[k].y > 3.5 && _GEMisc[k].y < 4.5;
    // Au-dessus de l'eau, les embruns sont freinés par l'air : le nuage reste plus près de l'impact que la poussière.
    // Pas de tir : la masse de vapeur déjà formée résiste au souffle d'une fusée qui monte (vidéos : le nuage reste
    // autour du pas) ; portée et vitesse du souffle radial limitées (en 1.5, il balayait tout le nuage hors de la grille).
    bool padK = _GEMisc[k].y > 1.5 && _GEMisc[k].y < 2.5;
    float reach = (water ? 0.4 : padK ? 0.32 : 0.6) * Rm;
    // Jet de paroi radial : u ∝ r_i / r (conservation de la quantité de mouvement), s'éteint vers R_max.
    // Sur un pas de tir, l'essentiel du jet part dans la tranchée : il ne reste qu'un souffle faible sur la table.
    float uRad = min(U * (water ? 0.5 : 1.0), padK ? 45.0 : 1e4) * (1.0 - 0.85 * channel);
    float ur = uRad * min(1.0, 1.4 * ri / max(r, 0.4 * ri)) * exp(-(r * r) / (reach * reach)) * smoothstep(0.0, 0.9 * ri, r) * asym;
    // Hauteur au-dessus du VRAI sol (carte des hauteurs) : le souffle rase le relief, monte les pentes, passe les buttes.
    float groundHere = GEGroundHeight(k, p.xz);
    float y = max(p.y - groundHere, 0.0);
    float delta = GEWallLayer(k, r);
    float fz = exp(-(y * y) / (delta * delta));
    float3 v;
    v.xz = dir * ur * fz;
    // Entraînement (poussière) : l'air au-dessus de la couche est aspiré vers le jet ; la poussière remonte le long
    // du lanceur et l'enveloppe. Faible pour la vapeur (sinon le dôme converge en cône).
    v.xz -= dir * lerp(0.05, 0.01, steam) * ur * exp(-y / (2.5 * delta)) * (1.0 - fz);
    // Soulèvement là où le jet pariétal décélère (le front s'enroule et monte) — jamais au centre, sous le jet.
    // Sur l'eau, la gerbe est projetée vers le haut à l'anneau (rideau d'embruns) puis retombe plus loin.
    float front = smoothstep(0.25, 0.9, r / reach);
    // Pas de tir : presque pas de soulèvement au front (il décollait toute la base du nuage de vapeur, qui flottait).
    v.y = ur * (water ? 0.9 : padK ? 0.06 : 0.35) * front * exp(-y / (4.0 * delta));
    // Rouleau du front (tourbillon torique posé au sol, juste derrière le front) : le bord du nuage se retourne sans
    // cesse au lieu de glisser d'un bloc. Intensité ∝ souffle qui arrive au front.
    float Rf = fx.w;
    if (full && Rf > 2.0 * ri && uRad > 0.5)
    {
        float aR = max(1.2 * GEWallLayer(k, Rf), 0.07 * Rf);
        float Rc = max(Rf - aR, 1.5 * ri);
        float uF = uRad * min(1.0, 1.4 * ri / Rc) * exp(-0.5 * (Rc * Rc) / (reach * reach));
        // Modéré (1.6) : un tourbillon trop fort retournait et diluait tout le nuage déjà formé au passage du front.
        float2 roll = GERoll(float2(r - Rc, y - 1.1 * aR), aR, 0.22 * uF);
        v.xz += dir * roll.x;
        v.y += roll.y;
    }
    // Bouches du déflecteur de flammes : jets rasants qui partent des bouches (le nuage naît là, pas sous la fusée).
    float outletSpeed = 0;
    if (full && channel > 0.001)
    {
        float s0, s1;
        v += channel * (GEOutletJet(k, 0, p, _GEOutlets[2 * k], _GEOutletGround[k].xy, Rm, s0)
                        + GEOutletJet(k, 1, p, _GEOutlets[2 * k + 1], _GEOutletGround[k].zw, Rm, s1));
        outletSpeed = channel * max(s0, s1);
    }
    // Flottabilité : la vapeur (et les gaz chauds) montent, de moins en moins avec l'altitude ; la poussière retombe.
    // Près du jet actif, le souffle rasant domine : la matière monte surtout au front et après la coupure.
    // Embruns au-dessus de l'eau : gouttelettes, presque pas de flottabilité (brume basse, pas un cumulus).
    // Pas de tir : la vapeur du déluge, chargée de gouttelettes, s'étale au sol et monte lentement jusqu'à
    // ~0,5 R_max, puis plafonne (vidéos de lancements) ; elle ne s'envole pas en boules.
    bool padSite = _GEMisc[k].y > 1.5 && _GEMisc[k].y < 2.5;
    // Pas de tir : la vapeur plafonne vers 0,25 R_max (≈ 65 m pour 7,6 MN) et au-dessus redescend un peu (air froid,
    // gouttelettes) : le nuage reste posé sur le sol au lieu de flotter en boule.
    // 1.8 : au bout des jets, plafond relevé jusqu'à ~0,5 R_max et flottabilité pleine (tours de vapeur) ; au départ
    // des jets et sur le reste du pas, la vapeur reste basse.
    float tip = padSite && full ? GEPadTip(k, p) : 0.0;
    float cTop = lerp(0.25, 0.4, tip) * Rm;
    float riseZone = padSite ? 1.0 - smoothstep(0.4 * cTop, cTop, y) - 0.3 * smoothstep(cTop, 1.6 * cTop, y)
                             : saturate(1.0 - y / max(1.8 * Rm, 30.0));
    float nearJet = max(jet.w * (1.0 - smoothstep(0.1, 0.6, r / reach)) * (1.0 - channel), saturate(outletSpeed / 25.0));
    // Poussière : soulevée par les gaz chauds du jet (≈ 1 m/s) ; vapeur : forte flottabilité.
    // Vidéos de lancements : le nuage de vapeur s'étale et monte lentement (quelques m/s), il ne file pas vers le ciel.
    float lift = lerp(1.3, 2.2, steam) * (0.35 + 0.65 * jet.w) + 0.4 * steam;
    // Embruns : entraînés vers le haut par les gaz chauds du jet (dôme), moins que la vapeur d'un pas de tir.
    v.y += lift * riseZone * (1.0 - 0.85 * nearJet * (1.0 - tip)) * (water ? 0.7 : padSite ? lerp(0.3, 0.95, tip) : 1.0);   // 1.9.3 : 0,3 aussi après le départ (0,1 : restes aplatis)
    v.y -= (1.0 - steam) * 0.35 * saturate(y / max(0.4 * Rm, 8.0)) * (1.0 - jet.w * 0.5);
    // Les gouttes retombent au-dessus d'un quart de R_max environ : sommet de dôme irrégulier, pas un plafond plat.
    if (water) v.y -= 1.4 * smoothstep(0.12 * Rm, 0.35 * Rm, y) * (1.0 - 0.5 * front);
    // Vent (profil croissant avec la hauteur).
    v.xz += fl.xy * saturate(0.3 + y / 25.0);
    // Relief : près du sol, l'écoulement suit la pente (il monte une butte au lieu de la traverser, descend dans un
    // creux) ; contre un obstacle raide (bâtiment, flanc du pas), il est dévié vers le haut.
    if (full && _GEGroundInfo[k].x > 0.5)
    {
        float cellG = 2.0 * max(_GEGridO[k].w, 1.0) / 48.0;
        float2 grad = float2(GEGroundHeight(k, p.xz + float2(cellG, 0)) - GEGroundHeight(k, p.xz - float2(cellG, 0)),
                             GEGroundHeight(k, p.xz + float2(0, cellG)) - GEGroundHeight(k, p.xz - float2(0, cellG))) / (2.0 * cellG);
        grad = clamp(grad, -3.0, 3.0);
        float nearG = exp(-y / max(2.0 * delta, 3.0));
        v.y += dot(v.xz, grad) * nearG;
        v.xz *= 1.0 - 0.5 * saturate(length(grad) - 1.0) * nearG;
    }
    return v;
}

float3 GEMeanVelocity(int k, float3 p) { return GEMeanVelocityEx(k, p, true); }

// Passage monde ↔ repère local de la grille.
void GEBasis(int k, out float3 east, out float3 up, out float3 north)
{
    up = normalize(_GEGridU[k].xyz);
    north = normalize(_GEGridN[k].xyz - up * dot(_GEGridN[k].xyz, up));
    east = cross(up, north);
}

// Coordonnées de texture (atlas de 4 grilles empilées en z) d'un point local.
float3 GEGridUv(int k, float3 p)
{
    float E = max(_GEGridO[k].w, 1.0), Hg = max(_GEGridU[k].w, 1.0);
    float3 uvw = float3(p.x / (2.0 * E) + 0.5, (p.y + 1.0) / (Hg + 1.0), p.z / (2.0 * E) + 0.5);
    return uvw;
}

#endif
