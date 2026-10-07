# Volumetric Ground FX (VGFX) - credits and code origin

VGFX is developed by Warnix. The rendering techniques referenced below inform the volumetric effects.

## Published techniques used

References for the algorithms used in the shaders and simulation:

- Real-time volumetric clouds, Perlin-Worley noise, "powder" effect: A. Schneider and N. Vos, *The Real-time Volumetric
  Cloudscapes of Horizon Zero Dawn*, SIGGRAPH 2015.
- Multiple scattering by octaves: M. Wrenninge et al., *Oz: The Great and Volumetric*, SIGGRAPH 2013.
- Henyey-Greenstein phase function (1941).
- Improved Perlin noise: K. Perlin, *Improving Noise*, SIGGRAPH 2002. Cellular noise: S. Worley, 1996.
- Semi-Lagrangian advection: J. Stam, *Stable Fluids*, SIGGRAPH 1999.
- Divergence-free turbulence (curl noise): R. Bridson et al., SIGGRAPH 2007.
- Lamb-Oseen vortex, turbulent jets and wall jets: classical fluid mechanics.
- Spatial hashing constants (73856093, 19349663, 83492791): M. Teschner et al., 2003.
- "lowbias32" integer hash: C. Wellons, public domain (*hash-prospector*).

## License

MIT - see `LICENSE`.
