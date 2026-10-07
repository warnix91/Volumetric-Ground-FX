using System;
using GroundBlastFx.Model;

namespace GroundBlastFx.Tests
{
    public static class PadPuffsTests
    {
        // Décollage type : deux bouches face à face à 12 m du centre, jet de sortie de 60 m/s, sol plat à 0.
        private static PadPuffs.Inputs Launch(float feed)
        {
            return new PadPuffs.Inputs
            {
                OutletCount = 2,
                O0X = 0f, O0Y = 2f, O0Z = 12f, D0X = 0f, D0Z = 1f,
                O1X = 0f, O1Y = 2f, O1Z = -12f, D1X = 0f, D1Z = -1f,
                OutletFeed = feed, ExitSpeed = 60f, OutletHalfWidth = 5f, JetLength = 120f,
                WindX = 3f, WindZ = 0f, CloudRadius = 260f, Steam = 1f, Life = 90f, Density = 1f, FadeTau = 27f,
            };
        }

        private static float Flat(float x, float z) => 0f;

        private static int Alive(PadPuffs p)
        {
            int n = 0;
            foreach (var q in p.Items) if (q.Alive) n++;
            return n;
        }

        [Test]
        public static void EmitsOnlyWhenTheOutletsAreFed()
        {
            var p = new PadPuffs(7);
            var idle = Launch(0f);
            for (int i = 0; i < 120; i++) p.Step(1f / 60f, ref idle, Flat);
            Assert.True(Alive(p) == 0, "pas de bouffée sans apport");
            var fed = Launch(1f);
            for (int i = 0; i < 120; i++) p.Step(1f / 60f, ref fed, Flat);
            Assert.True(Alive(p) >= 6, "plusieurs bouffées après 2 s de tir : " + Alive(p));
        }

        [Test]
        public static void PuffsLeaveAlongTheOutletAndBrakeToTheWind()
        {
            var p = new PadPuffs(11);
            var fed = Launch(1f);
            for (int i = 0; i < 30; i++) p.Step(1f / 60f, ref fed, Flat);
            var stop = Launch(0f);
            for (int i = 0; i < 60 * 20; i++) p.Step(1f / 60f, ref stop, Flat);
            bool any = false;
            foreach (var q in p.Items)
            {
                if (!q.Alive) continue;
                any = true;
                Assert.True(Math.Abs(q.Z) > 30f, "la bouffée s'est éloignée de sa bouche : z = " + q.Z);
                Assert.True(Math.Abs(q.Z) < 260f, "freinée avant d'aller au bout du monde : z = " + q.Z);
                Assert.Near(3.0, q.Vx, 0.6, "après 20 s, elle dérive avec le vent (x)");
                Assert.True(Math.Abs(q.Vz) < 1.0f, "la vitesse de sortie est retombée : vz = " + q.Vz);
            }
            Assert.True(any, "au moins une bouffée");
        }

        [Test]
        public static void PuffsStayOnTopOfTheGroundAndGrowWithinBounds()
        {
            var p = new PadPuffs(3);
            var fed = Launch(1f);
            Func<float, float, float> slope = (x, z) => 0.2f * z;   // pente : le sol monte vers le nord
            float[] last = new float[PadPuffs.Capacity];
            float[] lastAge = new float[PadPuffs.Capacity];
            for (int i = 0; i < 60 * 30; i++)
            {
                p.Step(1f / 60f, ref fed, slope);
                for (int k = 0; k < PadPuffs.Capacity; k++)
                {
                    var q = p.Items[k];
                    if (!q.Alive) { last[k] = 0f; continue; }
                    if (q.Age < lastAge[k]) last[k] = 0f;   // emplacement repris par une nouvelle bouffée (ou une fusion)
                    lastAge[k] = q.Age;
                    float g = slope(q.X, q.Z);
                    Assert.True(q.Y >= g + 0.2f * q.Radius - 1e-3f, "jamais enfoncée dans le sol");
                    Assert.True(q.Y <= g + 0.5f * q.Radius + 1e-3f, "toujours posée : le centre reste bas, aucune boule ne flotte");
                    Assert.True(q.Radius >= last[k] - 1e-4f || last[k] == 0f, "une bouffée ne rétrécit pas");
                    Assert.True(q.Radius <= 45f * 1.25f * 1.6f + 1e-3f, "rayon borné");
                    last[k] = q.Radius;
                }
            }
        }

        [Test]
        public static void CapacityIsRespectedAndPuffsEventuallyDie()
        {
            var p = new PadPuffs(5);
            var fed = Launch(1f);
            for (int i = 0; i < 60 * 60; i++) p.Step(1f / 60f, ref fed, Flat);
            Assert.True(Alive(p) <= PadPuffs.Capacity, "jamais plus que la capacité");
            Assert.True(Alive(p) > PadPuffs.Capacity / 2, "un long tir remplit le nuage : " + Alive(p));
            var stop = Launch(0f);
            for (int i = 0; i < 60 * 200; i++) p.Step(1f / 60f, ref stop, Flat);
            Assert.True(Alive(p) == 0, "tout s'est dissipé après la durée de vie : " + Alive(p));
        }

        [Test]
        public static void AfterTheFeedStopsTheCloudFadesWithTheJets()
        {
            // 1.0.1 (validé en jeu) : le nuage du pas part en même temps que les jets, ~30 s après le départ.
            var p = new PadPuffs(9);
            var fed = Launch(1f);
            for (int i = 0; i < 60 * 10; i++) p.Step(1f / 60f, ref fed, Flat);
            var stop = Launch(0f);
            for (int i = 0; i < 60 * 81; i++) p.Step(1f / 60f, ref stop, Flat);   // 3 constantes de temps
            float maxA = 0f;
            foreach (var q in p.Items) maxA = Math.Max(maxA, p.Opacity(q));
            Assert.True(maxA < 0.06f, "presque effacé après 3 × 27 s : " + maxA);
            for (int i = 0; i < 60 * 90; i++) p.Step(1f / 60f, ref stop, Flat);
            Assert.True(Alive(p) == 0, "plus rien après 6 × 27 s : " + Alive(p));
        }

        [Test]
        public static void ABiggerRocketMakesABiggerCloud()
        {
            // Starship (R_max ≈ 800 m) contre Falcon 9 (≈ 260 m) : plus de bouffées et plus loin, pas un petit tas au pied.
            var small = new PadPuffs(2); var big = new PadPuffs(2);
            var a = Launch(1f); var b = Launch(1f);
            a.ExitSpeed = b.ExitSpeed = 35f; a.OutletHalfWidth = b.OutletHalfWidth = 11.5f;   // valeurs du banc (S3, S4)
            a.JetLength = 379f; b.JetLength = 400f; b.CloudRadius = 800f;
            for (int i = 0; i < 60 * 8; i++) { small.Step(1f / 60f, ref a, Flat); big.Step(1f / 60f, ref b, Flat); }
            float reachA = 0f, reachB = 0f;
            foreach (var q in small.Items) if (q.Alive) reachA = Math.Max(reachA, Math.Abs(q.Z));
            foreach (var q in big.Items) if (q.Alive) reachB = Math.Max(reachB, Math.Abs(q.Z));
            Assert.True(Alive(big) >= 2 * Alive(small), "au moins deux fois plus de bouffées : " + Alive(big) + " contre " + Alive(small));
            Assert.True(reachB > 1.3f * reachA, "portée plus grande : " + reachB + " contre " + reachA);
            float tallA = 0f, tallB = 0f;
            foreach (var q in small.Items) if (q.Alive) tallA = Math.Max(tallA, q.Tall);
            foreach (var q in big.Items) if (q.Alive) tallB = Math.Max(tallB, q.Tall);
            Assert.Near(1.0, tallA, 1e-4, "Falcon 9 : bouffées rondes");
            Assert.True(tallB > 1.8f, "Starship : le nuage monte en tours : " + tallB);
            float minB = 9f; foreach (var q in big.Items) if (q.Alive && q.Age > 6f) minB = Math.Min(minB, q.Tall);
            Assert.True(tallB - minB > 0.4f, "tours de hauteurs variées : " + minB + " à " + tallB);
        }

        [Test]
        public static void TheCloudDoesNotFadeWhileTheJetFeedsIt()
        {
            var p = new PadPuffs(4);
            var fed = Launch(1f);
            for (int i = 0; i < 60 * 30; i++) p.Step(1f / 60f, ref fed, Flat);
            foreach (var q in p.Items)
                if (q.Alive) Assert.Near(1.0, q.Fade, 1e-4, "aucun fondu pendant le tir");
        }

        [Test]
        public static void OpacityFadesOutAtTheEndOfLife()
        {
            var p = new PadPuffs(1);
            var q = new PadPuffs.Puff { Alive = true, Density = 1f, Fade = 1f, Life = 100f, Age = 10f };
            Assert.Near(1.0, p.Opacity(q), 1e-4, "pleine opacité en début de vie");
            q.Age = 99.9f;
            Assert.True(p.Opacity(q) < 0.02f, "presque transparente en fin de vie");
        }
    }
}
