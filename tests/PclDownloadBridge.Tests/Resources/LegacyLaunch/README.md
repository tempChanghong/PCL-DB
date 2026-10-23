# Legacy launch fixtures

The two small JARs retain the original Mojang library bytes. Tests serve them
from loopback and verify them as files; tests do not execute a game or tweaker.

|Artifact|Official source|SHA-1|Bytes|
|---|---|---|---|
|launchwrapper-1.5.jar|https://libraries.minecraft.net/net/minecraft/launchwrapper/1.5/launchwrapper-1.5.jar|5150b9c2951f0fde987ce9c33496e26add1de224|27787|
|launchwrapper-1.6.jar|https://libraries.minecraft.net/net/minecraft/launchwrapper/1.6/launchwrapper-1.6.jar|4ea0aca9c022a234ebaf14b51fb119055955fc9d|27583|

The identities were checked against archived Mojang version metadata. Version
1.5 uses the default VanillaTweaker; the supported Classic profile uses 1.6 with
AlphaVanillaTweaker. The installation fixture's client and asset index remain
synthetic and do not establish that Minecraft can launch.

`ArgumentOracle.json` records 558 controlled argument strings and their expected
directory/tweaker result from the actual jopt-simple 4.5 parser (SHA-1
`6065cc95c661255349c1d0756657be17c29a4fd3`). Inputs first passed through the
split/dedup functions adapted from PCL commit
`5ef81e490ae46b1100d61e33d0183094c7a80ca8`, including its appended window
arguments. The oracle checks both supported wrapper families, short/abbreviated
long options, unknown options, literal values, and profile options. It is a finite
compatibility corpus, not a general command-line grammar specification.
