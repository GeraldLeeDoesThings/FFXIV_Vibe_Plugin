﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FFXIV_Vibe_Plugin.Device;

namespace FFXIV_Vibe_Plugin.Triggers {
  public class Trigger : IComparable<Trigger> {
    // First idea

    // General
    public int SortOder = -1;
    public readonly string Id = "";
    public string Name = "";

    // Device
    public Device.Device? Device = null;
    private int motorId = -1;


    private string kind = "Spell|Chat";
    private string trigger = "SpellDamage|SpellHeal|DamageRecieved|Miss|Mount";
    
    private bool incoming = false;
    private bool outgoing = true;
    private int minValue = 0;
    private int maxValue = 0;
    private string fromName = "me";
    private string toName = "any";
    private string action = "vibe|vibrate|rotate|linear|stop";
    private int duration = 2000;
    private string pattern = "default";
    /**
     * If it's damage, then check average overtime.
     * If it's heal, then check average overtime.
     */

    
    

    public Trigger(string name) {
      this.Id = Guid.NewGuid().ToString();
      this.Name = name;
    }

    public int CompareTo(Trigger other) {
      if(this.SortOder < other.SortOder) {
        return 1;
      } else if(this.SortOder > other.SortOder){
        return -1;
      } else {
        return 0;
      }
    }
  }
}
