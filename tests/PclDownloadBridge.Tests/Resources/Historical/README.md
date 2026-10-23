# Historical metadata fixtures

These are original archived UVMC profiles, including the three 2.0 colours and
thirty-one historical profiles. They are metadata only; client JARs are not embedded.
Canonical IDs use UVMC's separately mapped URL slugs for `b1.9-pre6`, `13w12~`
and 2.0. Installation fixtures use synthetic client/index bytes and real small
Mojang wrapper files; they do not prove that a game starts.

New raw profiles:

- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/1.0.0-rc1/1.0.0-rc1.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/inf-20100630-1/inf-20100630-1.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w10b/13w10b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w10a/13w10a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w09c/13w09c.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w09b/13w09b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/11w47a/11w47a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w09a/13w09a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w07a/13w07a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w11a/13w11a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w06a/13w06a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w05b/13w05b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w05a/13w05a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w04a/13w04a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w03a/13w03a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w02b/13w02b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w02a/13w02a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w01b/13w01b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/13w01a/13w01a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w50b/12w50b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w50a/12w50a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w49a/12w49a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w42b/12w42b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w42a/12w42a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w41b/12w41b.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w41a/12w41a.json
- https://zkitefly.github.io/unlisted-versions-of-minecraft/files/12w40b/12w40b.json

`AlphaLibraryReference.json` preserves the original Mojang `inf-20100618`
metadata bytes, SHA-1 `51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2`:
https://piston-meta.mojang.com/v1/packages/51a5c512af384d3d2a79a3efb93f7d4b9a1c6ec2/inf-20100618.json

It supplies download declarations only when all other library fields match.
Its pre-1.6 asset index, client and arguments are never imported into the target
profile. Missing indexes for the two new profiles use PCL's separately pinned
legacy declaration, rather than this reference's different resource tree.
