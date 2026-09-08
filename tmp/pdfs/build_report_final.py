from pathlib import Path
helpers=Path('tmp/pdfs/build_report_cognitive.py').read_text(encoding='utf-8-sig').split('W,H=A4;')[0]
exec(helpers.replace('Display-free board','Processing board'))
def leaf_polygon(c,cx,cy,length,width,fill):
    n=24;rear=-length*.5;bodyrear=-length*.30;tip=length*.5
    half=width*.5;stem=min(half*.32,max(.75,width*.055))
    pts=[(rear,-stem),(bodyrear,-stem)]
    for i in range(1,n+1):
        t=i/n;xx=bodyrear+(tip-bodyrear)*t
        env=max(0,math.sin(math.pi*t))**.72
        organic=min(1,.90+.06*math.sin(math.pi*t)+.04*math.sin(2*math.pi*t))
        pts.append((xx,-(stem*(1-t)+(half-stem)*env*organic)))
    for i in range(n-1,0,-1):
        t=i/n;xx=bodyrear+(tip-bodyrear)*t
        env=max(0,math.sin(math.pi*t))**.72
        organic=min(1,.94+.08*math.sin(math.pi*t)-.05*math.sin(2*math.pi*t))
        pts.append((xx,stem*(1-t)+(half-stem)*env*organic))
    pts.extend([(bodyrear,stem),(rear,stem)])
    p=c.beginPath();p.moveTo(cx+pts[0][0],cy+pts[0][1])
    for xx,yy in pts[1:]:p.lineTo(cx+xx,cy+yy)
    p.close();c.setFillColor(fill);c.drawPath(p,stroke=0,fill=1)
def nf_progression(c,x,y,width):
    c.saveState();c.translate(x,y);c.scale(width/515,width/515)
    centers=[64,193,322,451]
    titles=['Low NF','High NF','Sustained high NF','Color revealed']
    subtitles=['Dark interior','Bright interior','Green zone held 1 s','Group color']
    colors=['#060C13','#D9D9D9','#B3FFB3','#008CFF']
    for i,cx in enumerate(centers):
        label(c,titles[i],cx,81,8.7,True)
        c.setFillColor(HexColor('#102D3C'))
        c.roundRect(cx-43,27,86,44,4,stroke=0,fill=1)
        leaf_polygon(c,cx,49,59,35,white)
        leaf_polygon(c,cx,49,43,18,HexColor(colors[i]))
        label(c,subtitles[i],cx,13,7.7)
        if i<3:arrow(c,cx+49,49,centers[i+1]-49,49)
    c.restoreState()

W,H=A4;m=40;cw=W-2*m
c=canvas.Canvas(str(out/'Leaf_task_scientific_report.pdf'),pagesize=A4)
c.setTitle('SSVEP guided attention training with the Leaf task')
c.setAuthor('')
c.setSubject('Attention training and transfer to pointing and movement judgments')
y=H-42
c.setFillColor(black);c.setFont('ReportBold',19);c.drawString(m,y,'SSVEP guided attention training')
y-=20;c.setFont('Report',11);c.drawString(m,y,'Behavioral transfer to pointing and movement judgments')
y-=23
y=para(c,'The Leaf paradigm tests whether attention training with steady-state visual evoked potential (SSVEP) neurofeedback (NF) improves judgments of <b>pointing direction</b> (orientation) and <b>movement direction</b> (motion), and whether benefits differ between these features. Chinchani et al. found greater discrimination sensitivity for targets presented during high versus low SSVEP power states, supporting a behaviorally relevant attention marker [1]. Participants train to sustain selection of the cued leaf group among competing leaves before performing the direction judgment.',m,y,cw)
y-=13
left=289;gap=17;right=cw-left-gap
c.setFillColor(black);c.setFont('ReportBold',9)
c.drawString(m,y,'A   Closed loop attention training')
c.drawString(m+left+gap,y,'B   Behavioral task')
top=y-10
dh=diagram(c,m,top-left*2/3,left)
ih=right*551/1004
c.drawImage(str(out/'leaf_task_screencap.png'),m+left+gap,top-ih,width=right,height=ih)
para(c,'After color reveal, the Pointing cue requires the blue leaves\' pointing direction. A Moving cue requires the orange leaves\' movement direction.',m+left+gap,top-ih-7,right,caption)
y=top-dh-4
y=para(c,'<b>A</b> Electroencephalography (EEG) acquisition and analysis occur on a compact board. NF reaches the tablet over Wi-Fi in real time, and visual feedback guides continued attentional selection.',m,y,cw,caption)
y-=13
c.setFillColor(black);c.setFont('ReportBold',9)
c.drawString(m,y,'C   Neurofeedback driven leaf fill')
y-=90
nf_progression(c,m,y,cw)
y-=2
y=para(c,'Leaf interiors brighten as the NF level increases. Holding the green zone reveals each group\'s color, blue or orange, and enables the behavioral response. Outlines remain frequency-tagged.',m,y,cw,caption)
y-=11
y=para(c,'<b>Neurofeedback and task performance.</b> Inspired by Lumosity\'s <i>Ebb and Flow</i> [2], the task presents two intermingled groups with distinct pointing and movement directions, tagged at 19 and 23 Hz. A cue identifies the relevant group and feature. Participants first direct attention to the cued group to raise the feedback level. Maintaining the green zone for 1 s reveals the colored fills; a four-direction response is then required within 4 s. The task therefore separates attentional regulation from the subsequent behavioral judgment.',m,y,cw)
y-=7
y=para(c,'The NF signal is derived from normalized 19 and 23 Hz power in 1 s EEG windows, updated at approximately 100 ms intervals. A cue-dependent log contrast between the tags, adjusted against its within-trial history and smoothed, controls the shared leaf brightness. The signal reflects relative selection of the tagged groups, while response accuracy and latency from color reveal quantify performance on the selected feature.',m,y,cw)
y-=7
y=para(c,'<b>Behavioral transfer.</b> Training effects will be assessed by comparing accuracy and correct-response latency with a no-training condition matched for practice, stimulus exposure and reveal timing. The <b>training condition by judged feature interaction</b> tests whether benefits differ between pointing and movement. SSVEP modulation, NF attainment and time to criterion index attentional regulation and can be related to behavioral gains. Counterbalancing tag/color mappings limits stimulus confounds; joint neural and behavioral outcomes and a matched control support evaluation of NF-specific benefits [3].',m,y,cw)
y-=10
y=para(c,'<b>References</b><br/>'
    '[1] <link href="https://doi.org/10.1038/s42003-022-04231-w" color="#264D62">Chinchani, A. M., Paliwal, S., Ganesh, S. et al. (2022). Tracking momentary fluctuations in human attention with a cognitive brain-machine interface. <i>Communications Biology</i>, 5, 1346.</link><br/>'
    '[2] <link href="https://www.lumosity.com/en/brain-games/ebb-and-flow/" color="#264D62">Lumosity. <i>Ebb and Flow.</i></link> '
    '[3] <link href="https://doi.org/10.1093/brain/awaa009" color="#264D62">Ros et al. (2020). CRED-nf checklist. <i>Brain</i>, 143, 1674-1685.</link>',m,y,cw,source)
c.showPage();c.save()
d=canvas.Canvas(str(out/'leaf_nf_progression.pdf'),pagesize=(700,186))
d.setTitle('Neurofeedback driven leaf fill')
d.setFillColor(black);d.setFont('ReportBold',16);d.drawString(25,157,'Neurofeedback driven leaf fill')
nf_progression(d,25,28,650)
para(d,'Increasing NF brightens the leaf interior. The green zone must be held for 1 s before the group color is revealed.',25,19,650,caption)
d.showPage();d.save()
print('Report pages:',len(PdfReader(out/'Leaf_task_scientific_report.pdf').pages))
print('Final text bottom:',round(y,1))


