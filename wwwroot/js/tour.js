import Shepherd from 'https://cdn.jsdelivr.net/npm/shepherd.js/dist/esm/shepherd.mjs';

function addButtons(prevPartId, nextPartId) {
    const buttonsVar = [
        {
            action() {
                return this.back();
            },
            classes: 'shepherd-backbutton',
            text: '&lt;'
        },
        {
            action() {
                return this.next();
            },
            classes: 'shepherd-nextbutton',
            text: '&gt;'
        }
    ]
    if (prevPartId) {
        buttonsVar.push({
            action() {
                return this.show(prevPartId);
            },
            classes: 'shepherd-prevpartbutton',
            text: 'Back to previous part'
        });
    }
    if (nextPartId) {
        buttonsVar.push({
            action() {
                return this.show(nextPartId);
            },
            classes: 'shepherd-nextpartbutton',
            text: 'Skip to next part'
        });
    }
    return buttonsVar;
}

export function initiateTutorial() {
    const tour = new Shepherd.Tour({
        defaultStepOptions: {
            cancelIcon: {
                enabled: true
            },
            scrollTo: { behavior: 'smooth', block: 'center' }
        }
    });

    tour.addStep({
        title: 'Welcome',
        classes: 'shepherd-fullscreen',
        cancelIcon: {
            enabled: false
        },
        text: 'We recommend you follow this tutorial to get to know the basic features of MSP Challenge.',
        buttons: [
            {
                action() {
                    return this.cancel();
                },
                classes: 'btn btn-primary',
                text: 'Cancel'
            },
            {
                action() {
                    return this.next();
                },
                classes: 'btn btn-success',
                text: 'OK'
            }
        ],
        id: 'welcome'
    });

    tour.addStep({
        id: 'getstarted',
        title: 'Let\'s get started!',
        classes: 'shepherd-fullscreen',
        text: '<p>You are now a marine and maritime spatial planner! <br/>Your job is to create, implement and evaluate spatial plans for this area.</p><p>You will be working together with other players logged on to this session.</p>',
        buttons: addButtons('', ''),
    });

    tour.addStep({
        id: 'part1',
        title: 'Part 1<br/>Interface Basics',
        classes: 'shepherd-fullscreen',
        text: 'Before we start creating, implementing and evaluating spatial plans, let\'s walk through some basics.',
        buttons: addButtons('', 'part2'),
    });

    tour.addStep({
        id: 'interfacebasics1',
        title: 'Interface Basics [1/6]',
        classes: 'shepherd-bottomscreen',
        text: 'During this session you will govern the country associated with the colour you picked when logging in.',
        buttons: addButtons('', 'part2')        
    });

    tour.addStep({
        id: 'interfacebasics2',
        title: 'Interface Basics [2/6]',
        classes: 'shepherd-bottomscreen',
        text: 'Try zooming in and out with the scroll wheel of your mouse or by pinching on a trackpad or touchscreen.',
        buttons: addButtons('', 'part2'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const mapEl = document.getElementById('map');
                if (!nextBtn || !mapEl) return;

                // Start disabled & dimmed
                nextBtn.disabled = true;
                nextBtn.style.opacity = '0.2';

                const enable = () => {
                    nextBtn.disabled = false;
                    nextBtn.style.opacity = '1';
                    nextBtn.style.color = '#FFD700'; // Flash gold
                    nextBtn.animate(
                        [{ transform: 'scale(1)' }, { transform: 'scale(1.4)' }, { transform: 'scale(1)' }],
                        { duration: 400 }
                    );
                    this._cleanup();
                };

                const onWheel = () => enable();
                const onTouch = (e) => { if (e.touches.length > 1) enable(); };

                mapEl.addEventListener('wheel', onWheel, { passive: true, once: true });
                mapEl.addEventListener('touchmove', onTouch, { passive: true });

                this._cleanup = () => {
                    mapEl.removeEventListener('wheel', onWheel);
                    mapEl.removeEventListener('touchmove', onTouch);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'part2',
        title: 'Part 2<br/>Accessing Data & Exploring Consequences',
        classes: 'shepherd-fullscreen',
        text: 'Before you can start creating spatial plans, you first need to know what is already going on in this area of the ocean.',
        buttons: addButtons('part1', 'part3')
    });

    tour.start();
}