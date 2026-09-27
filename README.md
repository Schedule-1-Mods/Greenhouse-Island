# Greenhouse Island

(The internal folder, namespace and settings key are still `PelicanKey`.)

A **floating greenhouse** for Schedule I, moored off the **Docks**. It's a round timber platform
on white pontoons, with a glass geodesic dome in the middle and a big weed plant growing inside. You
walk to it: a walkway goes down from the shore and across the water straight onto the deck. It's
a **real property you buy from Ray's Realty for $0**.

- **Floating platform** (36 m across): a plank deck on a timber hull ringed with white pontoons, with lanterns and a ring of **the game's own trees** round the outer edge.
- **Greenhouse dome** (24 m across): glass panels on a white frame over a timber base wall. A **big weed plant** grows in the centre: a thick stalk, tiers of branches, seven-fingered, saw-toothed fan leaves with pale centre veins and frosty buds with orange hairs, in a black edging ring of soil and perlite.
- **Locked until you own it**: sliding glass doors fill the doorway. They stay shut ("Locked") until you buy the place, then open by themselves for you and your employees.
- **Save point by the door**, just inside, like the intercom at other properties. It only works once the property is yours.
- **Build area**: tiles cover the whole dome floor, all the way round to the wall, except the plant's planter.
- **Second floor**: a glass floor ring 4.2 m up, open in the middle round the weed plant (with a glass railing), reached by wooden stairs along the dome wall. It has its own build tiles all round, wherever there's standing room under the dome.
- **Deck ring**: hired employees wait here, behind the dome, until you give them a bed or locker. It also holds white water tanks, a wind turbine and a dumpster for cleaners. Employees: 30 by default (set `EmployeeCapacity`, 0-30).
- **Walkways**: from each shore point, stairs go down from the edge of the land (even a tall quay), then a floating railed walkway runs onto the platform. Where the quay has a railing, the walkway steps up and over it.

## Buying it

Talk to Ray, choose to buy a property, and pick **Greenhouse Island ($0)**. The for-sale
sign by the door comes down, the doors unlock, the save point switches on, and everything you
place in the dome is saved with the property.

## Where it is

Always in the same place for everyone: just off the Docks, with its two walkways starting on the
quay (see `FixedCenter` / `FixedWalkways` in Main.cs). It can't be moved in game.

Building is inside the dome only (ground floor and upstairs). The outside deck can't be built
on, except for one small patch right of the entrance where the cleaners' trash can goes.

There are no hotkeys: you walk there down the walkway from the Docks.

## Limits

- **Single player / host only**: the island property exists only in your game.
- **Removing the mod** leaves the property's data in your save, but the game can't find the property, so anything placed in the dome is lost. **Back up your save before trying it.**
