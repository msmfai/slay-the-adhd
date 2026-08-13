# Combat occlusion sweep — energy meter vs enemies

- Encounters enumerated: **95**
- Code-positioned (checkable, non-scene): **71**
- Scene-based (coordinates in packed .tscn — **un-checkable from the DLL**): **24**

Energy meter: centred, 200×200px nominal, vertical centre at height-fraction **1.03** (Config.EnergyCounterHeight default). Design space 1920×1080.

## At the default meter height
| nominal enemy | encounters where the meter occludes an enemy |
|---|---|
| small (200×260) | **0** / 71 |
| typical (300×360) | **0** / 71 |
| large (460×520) | **0** / 71 |

## Sensitivity to meter height (typical enemy)
| height fraction | occluding encounters |
|---|---|
| 0.55 | 8 / 71 |
| 0.60 | 8 / 71 |
| 0.65 | 8 / 71 |
| 0.70 | 8 / 71 |
| 0.75 | 7 / 71 |
| 0.80 | 0 / 71 |
| 0.85 | 0 / 71 |
| 1.03 | 0 / 71 |

## Occluding encounters at the DEFAULT meter height (typical enemy): 0
_none — at the default bottom position the meter clears every checkable enemy._

## Occluding encounters if the meter is raised to height 0.65 (typical enemy): 8
_(these are the crowded, multi-enemy fights whose enemies reach the screen centre; occlusion here is the "unavoidable" case)_

BattlewornDummyEventEncounter, ConstructMenagerieNormal, FlyconidNormal, InkletsNormal, RubyRaidersNormal, SlimesNormal, SlimesWeak, SlitheringStranglerNormal

## Scene-based encounters not checkable from the DLL: 24
_(their enemy positions are Marker2D nodes inside packed .tscn files; verifying these needs scene extraction)_

AxebotsNormal, BowlbugsNormal, BowlbugsWeak, DecimillipedeElite, DenseVegetationEventEncounter, ExoskeletonsNormal, ExoskeletonsWeak, FabricatorNormal, FakeMerchantEventEncounter, FogmogNormal, GremlinMercNormal, KaiserCrabBoss, KnightsElite, LivingFogNormal, MytesNormal, NibbitsNormal, OvicopterNormal, PhantasmalGardenersElite, PhrogParasiteElite, QueenBoss, SlumberingBeetleNormal, TheKinBoss, TheObscuraNormal, TwoTailedRatsNormal

