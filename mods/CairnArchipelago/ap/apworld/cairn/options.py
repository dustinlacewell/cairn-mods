from dataclasses import dataclass

from Options import DeathLink, PerGameCommonOptions, StartInventoryPool


@dataclass
class CairnOptions(PerGameCommonOptions):
    death_link: DeathLink
    start_inventory_from_pool: StartInventoryPool
